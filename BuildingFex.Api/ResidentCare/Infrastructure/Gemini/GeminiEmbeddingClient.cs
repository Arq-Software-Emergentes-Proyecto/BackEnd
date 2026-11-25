using System.Text;
using System.Text.Json;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.ResidentCare.Infrastructure.Gemini;

public interface IGeminiEmbeddingClient
{
    /// <param name="taskType">RETRIEVAL_DOCUMENT al indexar, RETRIEVAL_QUERY al consultar (null = sin taskType).</param>
    Task<float[]> EmbedAsync(string text, string? taskType = null, CancellationToken ct = default);
}

public static class EmbeddingTaskTypes
{
    public const string RetrievalDocument = "RETRIEVAL_DOCUMENT";
    public const string RetrievalQuery = "RETRIEVAL_QUERY";
}

public class GeminiEmbeddingClient(
    HttpClient http,
    IOptions<GeminiSettings> options,
    ILogger<GeminiEmbeddingClient> logger) : IGeminiEmbeddingClient
{
    public async Task<float[]> EmbedAsync(string text, string? taskType = null, CancellationToken ct = default)
    {
        var settings = options.Value;
        var apiKey = settings.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
            throw new GeminiException("GEMINI_API_KEY_MISSING", "Gemini API key is not configured.");

        var model = settings.ResolvedEmbeddingModel;

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return [];

        // Gemini embedding input limit ~8k tokens; keep chunks small upstream.
        if (trimmed.Length > 8000)
            trimmed = trimmed[..8000];

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:embedContent?key={Uri.EscapeDataString(apiKey)}");

        var body = new Dictionary<string, object>
        {
            ["model"] = $"models/{model}",
            ["content"] = new { parts = new[] { new { text = trimmed } } },
        };
        if (!string.IsNullOrWhiteSpace(taskType))
            body["taskType"] = taskType;
        // gemini-embedding-001 admite outputDimensionality (768 = mismo tamaño que los chunks ya indexados).
        if (settings.EmbeddingDimensions > 0 && !model.StartsWith("text-embedding-004", StringComparison.OrdinalIgnoreCase))
            body["outputDimensionality"] = settings.EmbeddingDimensions;

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 120)));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GeminiException("GEMINI_TIMEOUT", "Embedding timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Embedding network error");
            throw new GeminiException("GEMINI_UNAVAILABLE", "Embedding service unreachable.");
        }

        using var _ = response;
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            var msg = doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
                ? m.GetString()
                : $"HTTP {(int)response.StatusCode}";
            logger.LogWarning("Embedding failed: {Message}", msg);
            throw new GeminiException("GEMINI_EMBED_FAILED", msg ?? "Embedding failed");
        }

        if (!doc.RootElement.TryGetProperty("embedding", out var emb) ||
            !emb.TryGetProperty("values", out var values))
            return [];

        var list = new List<float>();
        foreach (var v in values.EnumerateArray())
            list.Add(v.GetSingle());
        return list.ToArray();
    }
}
