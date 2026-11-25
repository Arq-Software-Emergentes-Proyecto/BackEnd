using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Infrastructure.Storage;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;
using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using BuildingFex.Api.ResidentCare.Infrastructure.Gemini;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace BuildingFex.Api.ResidentCare.Application;

public interface IAiContextIndexer
{
    Task IndexUploadAsync(ImportUpload upload, CancellationToken ct = default);
    Task RemoveUploadAsync(string uploadExternalId, CancellationToken ct = default);
    Task<string> RetrieveContextAsync(string ownerAdminId, string query, int topK = 5, CancellationToken ct = default);

    /// <summary>
    /// Fragmentos relevantes de los documentos vigentes del edificio (solo OwnerAdminId). Lista vacía si
    /// ninguno supera el umbral de relevancia: el asistente debe derivar en vez de inventar.
    /// </summary>
    Task<IReadOnlyList<KnowledgeSource>> RetrieveAsync(string ownerAdminId, string query, int topK = 5, CancellationToken ct = default);

    /// <summary>Documentos vigentes con chunks de un modelo de embeddings distinto al configurado (re-indexar).</summary>
    Task<IReadOnlyList<string>> FindUploadsWithStaleEmbeddingsAsync(CancellationToken ct = default);
}

public class AiContextIndexer(
    AppDbContext db,
    IGeminiEmbeddingClient embeddings,
    IFirebaseStorageService storage,
    IOptions<GeminiSettings> geminiOptions,
    ILogger<AiContextIndexer> logger) : IAiContextIndexer
{
    private const int ChunkSize = 900;
    private const int ChunkOverlap = 120;
    /// <summary>Tope de chunks cargados en memoria por condominio para similitud coseno.</summary>
    private const int MaxChunksLoadedPerOwner = 400;

    public async Task IndexUploadAsync(ImportUpload upload, CancellationToken ct = default)
    {
        if (upload is null || string.IsNullOrWhiteSpace(upload.ExternalId))
            return;

        // RF033: solo documentos vigentes alimentan al asistente (no SUPERSEDED / ARCHIVED).
        if (!upload.IsCurrent || !await IsInContextFolderAsync(upload, ct))
        {
            await RemoveUploadAsync(upload.ExternalId, ct);
            upload.SetIndexingStatus(ImportIndexingStatus.None);
            await db.SaveChangesAsync(ct);
            return;
        }

        var hadChunks = await db.Set<AiContextChunk>().AnyAsync(c => c.UploadExternalId == upload.ExternalId, ct);
        var bytes = await LoadFileBytesAsync(upload, ct);
        if (bytes.Length == 0 && hadChunks)
        {
            // Re-indexado (p. ej. cambio de modelo) sin acceso al archivo: se conservan los chunks anteriores.
            logger.LogWarning("AI context re-index {File}: source file unavailable; keeping existing chunks", upload.FileName);
            upload.SetIndexingStatus(ImportIndexingStatus.Failed, "Source file unavailable");
            await db.SaveChangesAsync(ct);
            return;
        }

        var text = ExtractText(bytes, upload.MimeType, upload.FileName);
        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogInformation(
                "AI context skip {File}: no extractable text (mime {Mime})",
                upload.FileName,
                upload.MimeType);
            await RemoveUploadAsync(upload.ExternalId, ct);
            upload.SetIndexingStatus(ImportIndexingStatus.Failed, "No extractable text");
            await db.SaveChangesAsync(ct);
            return;
        }

        var model = geminiOptions.Value.ResolvedEmbeddingModel;
        var chunks = ChunkText(text).ToList();
        var fresh = new List<AiContextChunk>(chunks.Count);
        var i = 0;
        foreach (var chunk in chunks)
        {
            try
            {
                var vector = await embeddings.EmbedAsync(chunk, EmbeddingTaskTypes.RetrievalDocument, ct);
                if (vector.Length == 0) continue;

                fresh.Add(AiContextChunk.Create(
                    upload.OwnerAdminId,
                    upload.ExternalId,
                    upload.FileName,
                    i++,
                    chunk,
                    JsonSerializer.Serialize(vector),
                    model));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to embed chunk {Index} of {File}", i, upload.FileName);
            }
        }

        if (fresh.Count == 0 && chunks.Count > 0)
        {
            // Ningún chunk se pudo vectorizar (sin API key, Gemini caído): no se borran los anteriores.
            logger.LogWarning("AI context {File}: no chunk could be embedded; keeping previous index", upload.FileName);
            upload.SetIndexingStatus(ImportIndexingStatus.Failed, "Embedding failed");
            await db.SaveChangesAsync(ct);
            return;
        }

        // Reemplazo atómico: chunks anteriores fuera y nuevos dentro en un solo SaveChanges.
        var previous = await db.Set<AiContextChunk>()
            .Where(c => c.UploadExternalId == upload.ExternalId)
            .ToListAsync(ct);
        db.Set<AiContextChunk>().RemoveRange(previous);
        db.Set<AiContextChunk>().AddRange(fresh);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Indexed {Count} chunks for {File} ({Model})", fresh.Count, upload.FileName, model);
    }

    public async Task<IReadOnlyList<string>> FindUploadsWithStaleEmbeddingsAsync(CancellationToken ct = default)
    {
        var model = geminiOptions.Value.ResolvedEmbeddingModel;
        var staleUploads = db.Set<AiContextChunk>()
            .Where(c => c.EmbeddingModel != model)
            .Select(c => c.UploadExternalId)
            .Distinct();
        return await db.Set<ImportUpload>()
            .AsNoTracking()
            .Where(u => u.UploadStatus == ImportUploadStatus.Confirmed
                        && u.DocumentStatus == OfficialDocumentStatus.Published
                        && staleUploads.Contains(u.ExternalId))
            .Select(u => u.ExternalId)
            .ToListAsync(ct);
    }

    public async Task RemoveUploadAsync(string uploadExternalId, CancellationToken ct = default)
    {
        var id = (uploadExternalId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id)) return;

        var existing = await db.Set<AiContextChunk>()
            .Where(c => c.UploadExternalId == id)
            .ToListAsync(ct);
        if (existing.Count == 0) return;

        db.Set<AiContextChunk>().RemoveRange(existing);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> RetrieveContextAsync(
        string ownerAdminId,
        string query,
        int topK = 5,
        CancellationToken ct = default)
    {
        var ranked = await RetrieveAsync(ownerAdminId, query, topK, ct);
        if (ranked.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("BUILDING RULES / CONTEXT (from admin Contexto IA folder — treat as authoritative for this condominium):");
        foreach (var chunk in ranked)
        {
            sb.AppendLine($"--- source: {chunk.FileName} (relevance {chunk.Score:F2}) ---");
            sb.AppendLine(chunk.Content.Trim());
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    public async Task<IReadOnlyList<KnowledgeSource>> RetrieveAsync(
        string ownerAdminId,
        string query,
        int topK = 5,
        CancellationToken ct = default)
    {
        var owner = (ownerAdminId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(owner) || string.IsNullOrWhiteSpace(query))
            return [];

        // Filtra por tenant y por documentos vigentes (RF033: PUBLISHED; nunca SUPERSEDED/ARCHIVED ni
        // fragmentos huérfanos) y limita N para no cargar todo el corpus en memoria.
        var currentUploads = db.Set<ImportUpload>()
            .Where(u => u.OwnerAdminId == owner
                        && u.UploadStatus == ImportUploadStatus.Confirmed
                        && u.DocumentStatus == OfficialDocumentStatus.Published)
            .Select(u => u.ExternalId);
        var chunks = await db.Set<AiContextChunk>()
            .AsNoTracking()
            .Where(c => c.OwnerAdminId == owner && currentUploads.Contains(c.UploadExternalId))
            .OrderByDescending(c => c.CreatedAt)
            .Take(MaxChunksLoadedPerOwner)
            .ToListAsync(ct);

        if (chunks.Count == 0)
            return [];

        var k = Math.Clamp(topK, 1, 10);
        float[] queryVec;
        try
        {
            queryVec = await embeddings.EmbedAsync(query, EmbeddingTaskTypes.RetrievalQuery, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Query embed failed; falling back to keyword match");
            return KeywordFallback(chunks, query, k);
        }

        if (queryVec.Length == 0)
            return KeywordFallback(chunks, query, k);

        // Solo se comparan por coseno vectores del MISMO modelo que la consulta: con otro modelo (aunque tenga la
        // misma dimensión) la similitud no significa nada. Esos chunks se buscan por palabras clave.
        var model = geminiOptions.Value.ResolvedEmbeddingModel;
        var sameModel = chunks.Where(c => string.Equals(c.EmbeddingModel, model, StringComparison.OrdinalIgnoreCase)).ToList();
        var staleModel = chunks.Where(c => !string.Equals(c.EmbeddingModel, model, StringComparison.OrdinalIgnoreCase)).ToList();

        var minScore = Math.Clamp(geminiOptions.Value.MinRelevanceScore, 0, 0.99);
        var scored = sameModel
            .Select(c => (Chunk: c, Score: Cosine(queryVec, Deserialize(c.EmbeddingJson))))
            .ToList();

        // Vectores de otra dimensión (configuración cambiada) tampoco son comparables: keyword para ellos.
        var comparable = scored.Where(x => x.Score != 0).ToList();
        staleModel.AddRange(scored.Where(x => x.Score == 0).Select(x => x.Chunk));

        var results = comparable
            .Where(x => x.Score >= minScore)
            .Select(x => ToSource(x.Chunk, x.Score))
            .ToList();
        if (staleModel.Count > 0)
            results.AddRange(KeywordFallback(staleModel, query, k));

        return results
            .OrderByDescending(x => x.Score)
            .Take(k)
            .ToList();
    }

    private static KnowledgeSource ToSource(AiContextChunk chunk, double score) =>
        new(chunk.UploadExternalId,
            string.IsNullOrWhiteSpace(chunk.FileName) ? chunk.UploadExternalId : chunk.FileName,
            chunk.Content,
            score);

    private async Task<byte[]> LoadFileBytesAsync(ImportUpload upload, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(upload.StoragePath))
        {
            try
            {
                return await storage.DownloadBytesAsync(upload.StoragePath, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Admin SDK download failed for {Path}", upload.StoragePath);
            }
        }

        // Legacy only
        return DecodeDataUrl(upload.DataUrl);
    }

    private async Task<bool> IsInContextFolderAsync(ImportUpload upload, CancellationToken ct)
    {
        var folderId = (upload.FolderId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(folderId)) return false;

        var folder = await db.Set<ImportFolder>()
            .FirstOrDefaultAsync(
                f => f.OwnerAdminId == upload.OwnerAdminId
                     && (f.ExternalId == folderId || f.Id.ToString() == folderId),
                ct);

        return folder is { IsContext: true };
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "que", "los", "las", "del", "con", "para", "por", "una", "uno", "unos", "unas", "como", "cual", "cuales",
        "cuando", "donde", "esta", "este", "esto", "estos", "estas", "eso", "ese", "esa", "hay", "puedo", "puede",
        "pueden", "tengo", "tiene", "mis", "mas", "sus", "son", "ser", "hola", "gracias", "favor", "quiero", "saber",
        "the", "and", "for", "are", "can", "what", "when", "where", "how", "this", "that", "with", "have", "does",
        "you", "your", "my", "is", "do", "hello", "thanks", "please", "about", "want", "know",
    };

    private static IReadOnlyList<KnowledgeSource> KeywordFallback(List<AiContextChunk> chunks, string query, int topK)
    {
        var terms = Regex.Split(Fold(query), @"\W+")
            .Where(t => t.Length > 3 && !StopWords.Contains(t))
            .Distinct()
            .ToArray();
        if (terms.Length == 0) return [];

        return chunks
            .Select(c =>
            {
                var lower = Fold(c.Content);
                var hits = terms.Count(t => lower.Contains(t, StringComparison.Ordinal));
                return (Chunk: c, Hits: hits);
            })
            .Where(x => x.Hits > 0 && (terms.Length == 1 || x.Hits >= Math.Min(2, terms.Length)))
            .OrderByDescending(x => x.Hits)
            .Take(topK)
            .Select(x => ToSource(x.Chunk, Math.Round((double)x.Hits / terms.Length, 2)))
            .ToList();
    }

    private static string Fold(string text)
    {
        var normalized = (text ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ExtractText(byte[] bytes, string mimeType, string fileName)
    {
        if (bytes.Length == 0) return string.Empty;

        var mime = (mimeType ?? string.Empty).ToLowerInvariant();
        var name = (fileName ?? string.Empty).ToLowerInvariant();

        if (mime.StartsWith("text/") || name.EndsWith(".txt") || name.EndsWith(".md") ||
            name.EndsWith(".csv") || name.EndsWith(".json") || name.EndsWith(".log"))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (mime == "application/pdf" || name.EndsWith(".pdf"))
        {
            try
            {
                using var doc = PdfDocument.Open(bytes);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                    sb.AppendLine(page.Text);
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static byte[] DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return [];
        var idx = dataUrl.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        var b64 = idx >= 0 ? dataUrl[(idx + 7)..] : dataUrl;
        try
        {
            return Convert.FromBase64String(b64);
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> ChunkText(string text)
    {
        var cleaned = Regex.Replace(text, @"\r\n?", "\n");
        cleaned = Regex.Replace(cleaned, @"[ \t]+\n", "\n");
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n").Trim();
        if (cleaned.Length <= ChunkSize)
        {
            yield return cleaned;
            yield break;
        }

        var start = 0;
        while (start < cleaned.Length)
        {
            var len = Math.Min(ChunkSize, cleaned.Length - start);
            yield return cleaned.Substring(start, len).Trim();
            if (start + len >= cleaned.Length) break;
            start += ChunkSize - ChunkOverlap;
        }
    }

    private static float[] Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<float[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na <= 0 || nb <= 0) return 0;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
