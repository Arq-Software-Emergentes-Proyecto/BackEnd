using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.ResidentCare.Infrastructure.Gemini;

public interface IGeminiEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, string? taskType = null, CancellationToken ct = default);
}

public static class EmbeddingTaskTypes
{
    public const string RetrievalDocument = "RETRIEVAL_DOCUMENT";
    public const string RetrievalQuery = "RETRIEVAL_QUERY";
}

/// <summary>Cliente de embeddings de Gemini (embedContent). Sin circuit breaker ni reintentos.</summary>
public class GeminiEmbeddingClient(
    HttpClient http,
    IOptions<GeminiSettings> options,
    ILogger<GeminiEmbeddingClient> logger) : IGeminiEmbeddingClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const int MaxInputChars = 8000;

    private readonly GeminiSettings _settings = options.Value;

    public async Task<float[]> EmbedAsync(string text, string? taskType = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new GeminiException(GeminiException.ApiKeyMissing, "Gemini API key is not configured.");

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return [];
        if (trimmed.Length > MaxInputChars) trimmed = trimmed[..MaxInputChars];

        var model = _settings.ResolvedEmbeddingModel;
        var payload = new JsonObject
        {
            ["model"] = $"models/{model}",
            ["content"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = trimmed }),
            },
        };
        if (!string.IsNullOrWhiteSpace(taskType))
            payload["taskType"] = taskType.Trim();
        if (_settings.EmbeddingDimensions > 0 && !string.Equals(model, "text-embedding-004", StringComparison.OrdinalIgnoreCase))
            payload["outputDimensionality"] = _settings.EmbeddingDimensions;

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 1, 120));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        HttpResponseMessage response;
        string body;
        try
        {
            var url = $"{BaseUrl}{Uri.EscapeDataString(model)}:embedContent?key={Uri.EscapeDataString(_settings.ApiKey)}";
            response = await http.PostAsJsonAsync(url, payload, cts.Token);
            body = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GeminiException(GeminiException.Timeout, "Gemini embed request timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Gemini embed request failed.");
            throw new GeminiException(GeminiException.Unavailable, "Gemini embed network error.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = ExtractErrorMessage(body) ?? $"Gemini embed returned {(int)response.StatusCode}.";
            logger.LogWarning("Gemini embed {Model} failed with {Status}: {Message}", model, (int)response.StatusCode, message);
            throw new GeminiException("GEMINI_EMBED_FAILED", message);
        }

        return ExtractVector(body);
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static float[] ExtractVector(string body)
    {
        try
        {
            var values = JsonNode.Parse(body)?["embedding"]?["values"]?.AsArray();
            if (values is null) return [];
            var result = new float[values.Count];
            for (var i = 0; i < values.Count; i++)
                result[i] = values[i]?.GetValue<float>() ?? 0f;
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
