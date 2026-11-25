namespace BuildingFex.Api.ResidentCare.Infrastructure.Configuration;

public class GeminiSettings
{
    public const string SectionName = "Gemini";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Modelo de texto (informe 1.x "Costo de inteligencia artificial": Gemini 3.1 Flash-Lite;
    /// gemini-2.0-flash fue retirado el 1/06/2026). Si el id no existe en la API se prueban los de FallbackModels.
    /// </summary>
    public string Model { get; set; } = "gemini-3.1-flash-lite";

    /// <summary>Alternativas si el modelo configurado responde 404 / no disponible.</summary>
    public string[] FallbackModels { get; set; } =
    [
        "gemini-3.1-flash-lite-preview",
        "gemini-flash-lite-latest",
        "gemini-2.5-flash-lite",
    ];

    /// <summary>
    /// Embeddings del RAG (informe: gemini-embedding-001). Cada chunk guarda el modelo que lo generó
    /// (ai_context_chunks.embedding_model): al cambiarlo, los vectores viejos no se comparan por coseno (se usa
    /// coincidencia de palabras) y al arrancar se re-indexan los documentos vigentes con el modelo nuevo.
    /// </summary>
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";

    /// <summary>Id efectivo del modelo de embeddings (el configurado o gemini-embedding-001).</summary>
    public string ResolvedEmbeddingModel =>
        string.IsNullOrWhiteSpace(EmbeddingModel) ? "gemini-embedding-001" : EmbeddingModel.Trim();

    /// <summary>Dimensión de salida de gemini-embedding-001 (768 = compatible con los chunks antiguos).</summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>Similitud coseno mínima para considerar relevante un fragmento.</summary>
    public double MinRelevanceScore { get; set; } = 0.55;

    /// <summary>
    /// Timeout duro por llamada al proveedor. QAS05 fija ≤ 3 s como objetivo p95 de la respuesta al usuario, no
    /// como corte: con 3 s las respuestas lentas pero válidas (cold start, picos de Gemini) se cortaban y abrían el
    /// circuit breaker de todo el proceso. 10 s (configurable con Gemini__TimeoutSeconds) acota el peor caso.
    /// </summary>
    public double TimeoutSeconds { get; set; } = 10;

    /// <summary>Tokens de salida máximos por respuesta (informe: ≈ 400 por consulta).</summary>
    public int MaxOutputTokens { get; set; } = 512;

    /// <summary>Fallos consecutivos que abren el circuit breaker.</summary>
    public int CircuitBreakerFailures { get; set; } = 3;

    /// <summary>Segundos que el circuito permanece abierto (falla rápido sin llamar a Gemini).</summary>
    public int CircuitBreakerOpenSeconds { get; set; } = 30;

    /// <summary>Max generateContent calls per user (uid) per rolling minute.</summary>
    public int MaxRequestsPerMinute { get; set; } = 20;
}
