namespace BuildingFex.Api.ResidentCare.Infrastructure.Configuration;

public class GeminiSettings
{
    public const string SectionName = "Gemini";

    /// <summary>Clave de Google AI Studio. Vacía = el cliente falla solo al usarse (GEMINI_API_KEY_MISSING).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Modelo principal de generateContent.</summary>
    public string Model { get; set; } = "gemini-3.1-flash-lite";

    /// <summary>Modelos alternativos si el principal responde 404 o no está disponible.</summary>
    public string[] FallbackModels { get; set; } =
    [
        "gemini-3.1-flash-lite-preview",
        "gemini-flash-lite-latest",
        "gemini-2.5-flash-lite",
    ];

    /// <summary>Timeout por llamada, en segundos (se limita entre 1 y 120).</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Tope de tokens de salida por respuesta.</summary>
    public int MaxOutputTokens { get; set; } = 512;

    /// <summary>Fallos seguidos (>=500 o timeout) que abren el circuit breaker.</summary>
    public int CircuitBreakerFailures { get; set; } = 3;

    /// <summary>Segundos que el circuit breaker permanece abierto (falla rápido sin llamar).</summary>
    public int CircuitBreakerOpenSeconds { get; set; } = 30;

    /// <summary>Máximo de llamadas a Gemini por usuario (uid) en un minuto móvil (TS010).</summary>
    public int MaxRequestsPerMinute { get; set; } = 20;

    /// <summary>Modelo de embeddings para Contexto IA (US042).</summary>
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";

    /// <summary>Modelo efectivo de embeddings ("gemini-embedding-001" si viene vacío).</summary>
    public string ResolvedEmbeddingModel =>
        string.IsNullOrWhiteSpace(EmbeddingModel) ? "gemini-embedding-001" : EmbeddingModel.Trim();

    /// <summary>Dimensiones de salida solicitadas para embeddings (outputDimensionality).</summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>Puntaje mínimo de relevancia (coseno o palabras clave) para usar un fragmento en el RAG.</summary>
    public double MinRelevanceScore { get; set; } = 0.55;
}
