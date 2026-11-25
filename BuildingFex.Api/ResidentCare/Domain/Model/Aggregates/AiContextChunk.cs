namespace BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;

/// <summary>
/// Chunk indexado de un documento en la carpeta Contexto IA (RAG).
/// </summary>
public class AiContextChunk
{
    /// <summary>Modelo con el que se indexaron los chunks anteriores a la columna embedding_model.</summary>
    public const string LegacyEmbeddingModel = "text-embedding-004";

    public int Id { get; private set; }
    public string OwnerAdminId { get; private set; } = string.Empty;
    public string UploadExternalId { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public int ChunkIndex { get; private set; }
    public string Content { get; private set; } = string.Empty;
    /// <summary>Embedding serializado como JSON array de floats.</summary>
    public string EmbeddingJson { get; private set; } = "[]";

    /// <summary>Modelo de embeddings que generó el vector: vectores de modelos distintos no son comparables.</summary>
    public string EmbeddingModel { get; private set; } = LegacyEmbeddingModel;
    public DateTimeOffset CreatedAt { get; private set; }

    private AiContextChunk()
    {
    }

    /// <param name="embeddingModel">Modelo que generó <paramref name="embeddingJson"/> (null = legado text-embedding-004).</param>
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
            OwnerAdminId = ownerAdminId.Trim(),
            UploadExternalId = uploadExternalId.Trim(),
            FileName = (fileName ?? string.Empty).Trim(),
            ChunkIndex = chunkIndex,
            Content = content ?? string.Empty,
            EmbeddingJson = string.IsNullOrWhiteSpace(embeddingJson) ? "[]" : embeddingJson,
            EmbeddingModel = string.IsNullOrWhiteSpace(embeddingModel) ? LegacyEmbeddingModel : embeddingModel.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
