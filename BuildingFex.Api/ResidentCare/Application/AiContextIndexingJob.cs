using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.ResidentCare.Application;

/// <summary>
/// Indexación de un documento de Contexto IA. La usa el worker en background (servidor normal) y la
/// función de jobs en AWS Lambda (invocación asíncrona por documento + barrido programado).
/// </summary>
public class AiContextIndexingJob(
    AppDbContext db,
    IAiContextIndexer indexer,
    IOptions<GeminiSettings> geminiOptions,
    ILogger<AiContextIndexingJob> logger)
{
    /// <summary>
    /// Documentos que hay que (re)indexar: confirmados que siguen en "pending" (p. ej. la invocación se perdió)
    /// y vigentes con chunks de otro modelo de embeddings. Sin API key de Gemini no devuelve nada.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindWorkAsync(bool includePending, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(geminiOptions.Value.ApiKey))
            return [];

        var ids = new List<string>();
        if (includePending)
        {
            ids.AddRange(await db.Set<ImportUpload>()
                .AsNoTracking()
                .Where(u => u.UploadStatus == ImportUploadStatus.Confirmed
                            && u.IndexingStatus == ImportIndexingStatus.Pending)
                .OrderBy(u => u.UploadedAt)
                .Select(u => u.ExternalId)
                .Take(50)
                .ToListAsync(ct));
        }

        try
        {
            ids.AddRange(await indexer.FindUploadsWithStaleEmbeddingsAsync(ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AI context: stale embedding scan skipped");
        }

        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task ProcessAsync(string uploadId, CancellationToken ct)
    {
        var upload = await db.Set<ImportUpload>()
            .FirstOrDefaultAsync(u => u.ExternalId == uploadId, ct);
        if (upload is null) return;
        if (upload.UploadStatus != ImportUploadStatus.Confirmed)
        {
            logger.LogInformation("Skip indexing {UploadId}: not confirmed yet", uploadId);
            return;
        }

        upload.SetIndexingStatus(ImportIndexingStatus.Indexing);
        await db.SaveChangesAsync(ct);

        try
        {
            await indexer.IndexUploadAsync(upload, ct);
            upload = await db.Set<ImportUpload>()
                .FirstAsync(u => u.ExternalId == uploadId, ct);
            // Indexer puede dejar none/failed; solo marcar done si seguía indexing.
            if (upload.IndexingStatus is ImportIndexingStatus.Indexing or ImportIndexingStatus.Pending)
            {
                upload.SetIndexingStatus(ImportIndexingStatus.Done);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Indexing error {UploadId}", uploadId);
            upload = await db.Set<ImportUpload>()
                .FirstOrDefaultAsync(u => u.ExternalId == uploadId, ct);
            if (upload is not null)
            {
                upload.SetIndexingStatus(ImportIndexingStatus.Failed, ex.Message);
                await db.SaveChangesAsync(ct);
            }
        }
    }
}
