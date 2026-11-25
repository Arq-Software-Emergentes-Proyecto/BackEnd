namespace BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;

/// <summary>Fragmento indexado de un documento de Contexto IA (tabla ai_context_chunks).</summary>
public class AiContextChunk
{
    public const string LegacyEmbeddingModel = "text-embedding-004";

    public int Id { get; private set; }
    public string OwnerAdminId { get; private set; } = string.Empty;
    public string UploadExternalId { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public int ChunkIndex { get; private set; }
    public string Content { get; private set; } = string.Empty;
    /// <summary>Array JSON de floats.</summary>
    public string EmbeddingJson { get; private set; } = "[]";
    /// <summary>Modelo de embeddings usado: vectores de modelos distintos no se comparan por coseno.</summary>
    public string EmbeddingModel { get; private set; } = LegacyEmbeddingModel;
    public DateTimeOffset CreatedAt { get; private set; }

    private AiContextChunk() { }

    public static AiContextChunk Create(
        string ownerAdminId,
        string uploadExternalId,
        string fileName,
        int chunkIndex,
        string content,
        string embeddingJson,
        string? embeddingModel = null)
    {
        return new AiContextChunk
        {
            OwnerAdminId = (ownerAdminId ?? string.Empty).Trim(),
            UploadExternalId = (uploadExternalId ?? string.Empty).Trim(),
            FileName = (fileName ?? string.Empty).Trim(),
            ChunkIndex = chunkIndex,
            Content = (content ?? string.Empty).Trim(),
            EmbeddingJson = string.IsNullOrWhiteSpace(embeddingJson) ? "[]" : embeddingJson.Trim(),
            EmbeddingModel = string.IsNullOrWhiteSpace(embeddingModel) ? LegacyEmbeddingModel : embeddingModel.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
