using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.ResidentCare.Application;

/// <summary>Trabajo de indexación compartido por el worker local y la Lambda de jobs (ai-index).</summary>
public class AiContextIndexingJob(
    AppDbContext db,
    IAiContextIndexer indexer,
    IOptions<GeminiSettings> options,
    ILogger<AiContextIndexingJob> logger)
{
    public async Task<IReadOnlyList<string>> FindWorkAsync(bool includePending, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
            return [];

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (includePending)
        {
            var pending = await db.Set<ImportUpload>()
                .AsNoTracking()
                .Where(u => u.UploadStatus == ImportUploadStatus.Confirmed
                            && u.IndexingStatus == ImportIndexingStatus.Pending)
                .OrderBy(u => u.UploadedAt)
                .ThenBy(u => u.Id)
                .Take(50)
                .Select(u => u.ExternalId)
                .ToListAsync(ct);

            foreach (var id in pending)
            {
                if (seen.Add(id)) ids.Add(id);
            }
        }

        try
        {
            var stale = await indexer.FindUploadsWithStaleEmbeddingsAsync(ct);
            foreach (var id in stale)
            {
                if (seen.Add(id)) ids.Add(id);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "FindUploadsWithStaleEmbeddings failed");
        }

        return ids;
    }

    public async Task ProcessAsync(string uploadExternalId, CancellationToken ct = default)
    {
        var clean = (uploadExternalId ?? string.Empty).Trim();
        if (clean.Length == 0) return;

        var upload = await db.Set<ImportUpload>()
            .FirstOrDefaultAsync(u => u.ExternalId == clean || u.Id.ToString() == clean, ct);
        if (upload is null) return;

        if (upload.UploadStatus != ImportUploadStatus.Confirmed)
        {
            logger.LogDebug("Skipping indexing for unconfirmed upload {UploadId}", upload.ExternalId);
            return;
        }

        upload.SetIndexingStatus(ImportIndexingStatus.Indexing);
        await db.SaveChangesAsync(ct);

        try
        {
            await indexer.IndexUploadAsync(upload, ct);
            await db.Entry(upload).ReloadAsync(ct);
            if (upload.IndexingStatus is ImportIndexingStatus.Indexing or ImportIndexingStatus.Pending)
            {
                upload.SetIndexingStatus(ImportIndexingStatus.Done);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Indexing failed for {UploadId}", upload.ExternalId);
            upload.SetIndexingStatus(ImportIndexingStatus.Failed, ex.Message);
            await db.SaveChangesAsync(ct);
        }
    }
}
