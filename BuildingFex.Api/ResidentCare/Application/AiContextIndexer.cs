using System.Globalization;
using System.Text;
using System.Text.Json;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Infrastructure.Storage;
using BuildingFex.Api.ResidentCare.Domain;
using BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;
using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using BuildingFex.Api.ResidentCare.Infrastructure.Gemini;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace BuildingFex.Api.ResidentCare.Application;

public interface IAiContextIndexer
{
    Task IndexUploadAsync(ImportUpload upload, CancellationToken ct = default);
    Task RemoveUploadAsync(string uploadExternalId, CancellationToken ct = default);
    Task<string> RetrieveContextAsync(string ownerAdminId, string query, int topK = 5, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeSource>> RetrieveAsync(string ownerAdminId, string query, int topK = 5, CancellationToken ct = default);
    Task<IReadOnlyList<string>> FindUploadsWithStaleEmbeddingsAsync(CancellationToken ct = default);
}

/// <summary>Indexación y recuperación RAG por edificio sobre documentos vigentes en la carpeta Contexto IA.</summary>
public class AiContextIndexer(
    AppDbContext db,
    IGeminiEmbeddingClient embeddings,
    IFirebaseStorageService storage,
    IOptions<GeminiSettings> options,
    ILogger<AiContextIndexer> logger) : IAiContextIndexer
{
    private const int ChunkSize = 900;
    private const int ChunkOverlap = 120;
    private const int MaxChunksLoadedPerOwner = 400;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "como", "para", "pero", "porque", "cuando", "donde", "quien", "quienes", "cual", "cuales",
        "esta", "este", "esto", "estos", "estas", "esos", "esas", "hola", "gracias", "buenos",
        "dias", "tardes", "noches", "favor", "sobre", "entre", "hasta", "desde", "tiene", "tienen",
        "puedo", "puede", "pueden", "todo", "todos", "toda", "todas", "algo", "nada", "edificio",
        "condominio", "that", "this", "with", "from", "what", "when", "where", "which", "who",
        "have", "has", "had", "will", "would", "could", "should", "hello", "thanks", "thank",
        "please", "about", "into", "your", "they", "them", "their", "there", "here", "some", "more",
    };

    private readonly GeminiSettings _settings = options.Value;

    public async Task IndexUploadAsync(ImportUpload upload, CancellationToken ct = default)
    {
        var inContext = upload.IsCurrent
            && !string.IsNullOrWhiteSpace(upload.FolderId)
            && await db.Set<ImportFolder>().AnyAsync(
                f => f.OwnerAdminId == upload.OwnerAdminId
                     && f.IsContext
                     && (f.ExternalId == upload.FolderId || f.Id.ToString() == upload.FolderId),
                ct);

        if (!inContext)
        {
            await RemoveUploadAsync(upload.ExternalId, ct);
            upload.SetIndexingStatus(ImportIndexingStatus.None);
            await db.SaveChangesAsync(ct);
            return;
        }

        var bytes = await DownloadBytesWithFallbackAsync(upload, ct);
        if (bytes is null || bytes.Length == 0)
        {
            upload.SetIndexingStatus(ImportIndexingStatus.Failed, "Source file unavailable");
            await db.SaveChangesAsync(ct);
            return;
        }

        var text = ExtractText(upload, bytes);
        if (string.IsNullOrWhiteSpace(text))
        {
            await RemoveUploadAsync(upload.ExternalId, ct);
            upload.SetIndexingStatus(ImportIndexingStatus.Failed, "No extractable text");
            await db.SaveChangesAsync(ct);
            return;
        }

        var chunks = ChunkText(text);
        var model = _settings.ResolvedEmbeddingModel;
        var built = new List<AiContextChunk>();

        for (var i = 0; i < chunks.Count; i++)
        {
            try
            {
                var vec = await embeddings.EmbedAsync(chunks[i], EmbeddingTaskTypes.RetrievalDocument, ct);
                if (vec.Length == 0) continue;
                var json = JsonSerializer.Serialize(vec);
                built.Add(AiContextChunk.Create(
                    upload.OwnerAdminId,
                    upload.ExternalId,
                    upload.FileName,
                    i,
                    chunks[i],
                    json,
                    model));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Chunk {Index} embedding skipped for {UploadId}", i, upload.ExternalId);
            }
        }

        if (built.Count == 0)
        {
            // Conserva los chunks anteriores si ya existían.
            upload.SetIndexingStatus(ImportIndexingStatus.Failed, "Embedding failed");
            await db.SaveChangesAsync(ct);
            return;
        }

        var previous = await db.Set<AiContextChunk>()
            .Where(c => c.UploadExternalId == upload.ExternalId)
            .ToListAsync(ct);
        if (previous.Count > 0)
            db.Set<AiContextChunk>().RemoveRange(previous);

        db.Set<AiContextChunk>().AddRange(built);
        upload.SetIndexingStatus(ImportIndexingStatus.Done);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveUploadAsync(string uploadExternalId, CancellationToken ct = default)
    {
        var clean = (uploadExternalId ?? string.Empty).Trim();
        if (clean.Length == 0) return;

        var rows = await db.Set<AiContextChunk>()
            .Where(c => c.UploadExternalId == clean)
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        db.Set<AiContextChunk>().RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> RetrieveContextAsync(string ownerAdminId, string query, int topK = 5, CancellationToken ct = default)
    {
        var sources = await RetrieveAsync(ownerAdminId, query, topK, ct);
        if (sources.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("BUILDING RULES / CONTEXT (use only these current documents):");
        foreach (var s in sources)
        {
            sb.Append("--- source: ").Append(s.FileName)
                .Append(" (relevance ").Append(s.Score.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(") ---");
            sb.AppendLine(s.Content);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<IReadOnlyList<KnowledgeSource>> RetrieveAsync(
        string ownerAdminId,
        string query,
        int topK = 5,
        CancellationToken ct = default)
    {
        var owner = (ownerAdminId ?? string.Empty).Trim();
        if (owner.Length == 0 || string.IsNullOrWhiteSpace(query)) return [];

        var limit = Math.Clamp(topK, 1, 10);
        var minScore = Math.Clamp(_settings.MinRelevanceScore, 0.0, 0.99);

        var currentUploads = db.Set<ImportUpload>()
            .AsNoTracking()
            .Where(u => u.OwnerAdminId == owner
                        && u.UploadStatus == ImportUploadStatus.Confirmed
                        && u.DocumentStatus == OfficialDocumentStatus.Published)
            .Select(u => u.ExternalId);

        var chunks = await db.Set<AiContextChunk>()
            .AsNoTracking()
            .Where(c => c.OwnerAdminId == owner && currentUploads.Contains(c.UploadExternalId))
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(MaxChunksLoadedPerOwner)
            .ToListAsync(ct);

        if (chunks.Count == 0) return [];

        float[] queryVec = [];
        try
        {
            queryVec = await embeddings.EmbedAsync(query, EmbeddingTaskTypes.RetrievalQuery, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Query embedding failed; falling back to keyword match");
        }

        var targetModel = _settings.ResolvedEmbeddingModel;
        var scored = new List<KnowledgeSource>();

        foreach (var chunk in chunks)
        {
            double score;
            if (queryVec.Length > 0
                && string.Equals(chunk.EmbeddingModel, targetModel, StringComparison.OrdinalIgnoreCase))
            {
                var vec = ParseVector(chunk.EmbeddingJson);
                score = vec.Length == queryVec.Length
                    ? CosineSimilarity(queryVec, vec)
                    : KeywordScore(query, chunk.Content);
            }
            else
            {
                score = KeywordScore(query, chunk.Content);
            }

            if (score >= minScore)
            {
                scored.Add(new KnowledgeSource(
                    chunk.UploadExternalId,
                    chunk.FileName,
                    chunk.Content,
                    Math.Round(score, 4)));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> FindUploadsWithStaleEmbeddingsAsync(CancellationToken ct = default)
    {
        var targetModel = _settings.ResolvedEmbeddingModel;

        var currentInContext =
            from u in db.Set<ImportUpload>().AsNoTracking()
            join f in db.Set<ImportFolder>().AsNoTracking()
                on u.OwnerAdminId equals f.OwnerAdminId
            where u.UploadStatus == ImportUploadStatus.Confirmed
                  && u.DocumentStatus == OfficialDocumentStatus.Published
                  && f.IsContext
                  && u.FolderId != null
                  && (f.ExternalId == u.FolderId || f.Id.ToString() == u.FolderId)
            select u.ExternalId;

        return await db.Set<AiContextChunk>()
            .AsNoTracking()
            .Where(c => currentInContext.Contains(c.UploadExternalId) && c.EmbeddingModel != targetModel)
            .Select(c => c.UploadExternalId)
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task<byte[]?> DownloadBytesWithFallbackAsync(ImportUpload upload, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(upload.StoragePath) && storage.IsConfigured)
        {
            try
            {
                return await storage.DownloadBytesAsync(upload.StoragePath, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Storage download failed for {UploadId} ({Path}); trying legacy dataUrl",
                    upload.ExternalId, upload.StoragePath);
            }
        }

        if (!string.IsNullOrWhiteSpace(upload.DataUrl))
        {
            var comma = upload.DataUrl.IndexOf(',');
            var base64 = comma >= 0 ? upload.DataUrl[(comma + 1)..] : upload.DataUrl;
            try
            {
                return Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    private string ExtractText(ImportUpload upload, byte[] bytes)
    {
        var mime = (upload.MimeType ?? string.Empty).Trim().ToLowerInvariant();
        var ext = Path.GetExtension(upload.FileName ?? string.Empty).ToLowerInvariant();

        if (mime.StartsWith("text/", StringComparison.Ordinal)
            || ext is ".txt" or ".md" or ".csv" or ".json" or ".log")
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (mime == "application/pdf" || ext == ".pdf")
        {
            try
            {
                using var doc = PdfDocument.Open(bytes);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                {
                    var pageText = ContentOrderTextExtractor.GetText(page);
                    if (!string.IsNullOrWhiteSpace(pageText))
                        sb.AppendLine(pageText);
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PDF extraction failed for {UploadId}", upload.ExternalId);
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static List<string> ChunkText(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0) return [];
        if (normalized.Length <= ChunkSize) return [normalized];

        var result = new List<string>();
        var step = ChunkSize - ChunkOverlap;
        for (var start = 0; start < normalized.Length; start += step)
        {
            var length = Math.Min(ChunkSize, normalized.Length - start);
            var slice = normalized.Substring(start, length).Trim();
            if (slice.Length > 0)
                result.Add(slice);
            if (start + length >= normalized.Length) break;
        }
        return result;
    }

    private static float[] ParseVector(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<float[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0.0;
    }

    private static double KeywordScore(string query, string content)
    {
        var terms = SupportEscalate.Fold(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3 && !Stopwords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (terms.Count == 0) return 0.0;

        var foldedContent = $" {SupportEscalate.Fold(content)} ";
        var hits = terms.Count(t => foldedContent.Contains($" {t} ", StringComparison.Ordinal)
                                    || foldedContent.Contains(t, StringComparison.Ordinal));
        if (terms.Count >= 2 && hits < 2) return 0.0;

        return Math.Round((double)hits / terms.Count, 2);
    }
}
