using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.ResidentCare.Infrastructure.Gemini;

/// <summary>Un turno de la conversación. Role: "resident"/"user" → user; el resto → model.</summary>
public record GeminiTurn(string Role, string Body);

public interface IGeminiClient
{
    Task<string> GenerateReplyAsync(IReadOnlyList<GeminiTurn> history, string systemInstruction, CancellationToken ct = default);
    Task<string> SummarizeTitleAsync(IReadOnlyList<GeminiTurn> history, string locale, CancellationToken ct = default);
}

/// <summary>Error del cliente con código estable para que el controller responda sin guardar el mensaje.</summary>
public class GeminiException(string code, string message) : Exception(message)
{
    public const string ApiKeyMissing = "GEMINI_API_KEY_MISSING";
    public const string EmptyPrompt = "GEMINI_EMPTY_PROMPT";
    public const string Unavailable = "GEMINI_UNAVAILABLE";
    public const string Timeout = "GEMINI_TIMEOUT";
    public const string RequestFailed = "GEMINI_REQUEST_FAILED";
    public const string EmptyReply = "GEMINI_EMPTY_REPLY";
    public const string Busy = "GEMINI_BUSY";

    public string Code { get; } = code;

    /// <summary>true = el cliente puede reintentar luego sin duplicar nada.</summary>
    public bool IsTransient => Code is Timeout or Unavailable or Busy or RequestFailed or EmptyReply;
}

/// <summary>Circuit breaker estático (QAS05): se abre tras N fallos seguidos y falla rápido.</summary>
public static class GeminiCircuitBreaker
{
    private static readonly object Gate = new();
    private static int _failures;
    private static DateTime _openUntilUtc = DateTime.MinValue;

    public static bool IsOpen
    {
        get { lock (Gate) return DateTime.UtcNow < _openUntilUtc; }
    }

    public static void RecordSuccess()
    {
        lock (Gate) { _failures = 0; _openUntilUtc = DateTime.MinValue; }
    }

    public static void RecordFailure(int threshold, int openSeconds)
    {
        lock (Gate)
        {
            if (++_failures < Math.Max(1, threshold)) return;
            _openUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, openSeconds));
            _failures = 0;
        }
    }
}

public class GeminiClient(
    HttpClient http,
    IOptions<GeminiSettings> options,
    ILogger<GeminiClient> logger) : IGeminiClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    // Modelo que ya respondió bien (tras un fallback); se comparte entre instancias.
    private static string? _workingModel;

    private readonly GeminiSettings _settings = options.Value;

    public Task<string> GenerateReplyAsync(IReadOnlyList<GeminiTurn> history, string systemInstruction, CancellationToken ct = default)
        => CallAsync(history, systemInstruction, accessory: false, ct);

    public async Task<string> SummarizeTitleAsync(IReadOnlyList<GeminiTurn> history, string locale, CancellationToken ct = default)
    {
        // Accesoria: si el circuito está abierto no se intenta.
        if (GeminiCircuitBreaker.IsOpen)
            throw new GeminiException(GeminiException.Busy, "Gemini circuit is open.");
        var lang = locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "English" : "Spanish";
        var instruction = $"Write a title of at most 6 words in {lang} that summarizes this conversation. Reply with the title only, no quotes.";
        return (await CallAsync(history, instruction, accessory: true, ct)).Trim().Trim('"');
    }

    private async Task<string> CallAsync(IReadOnlyList<GeminiTurn> history, string systemInstruction, bool accessory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new GeminiException(GeminiException.ApiKeyMissing, "Gemini API key is not configured.");
        if (history.Count == 0 || history.All(t => string.IsNullOrWhiteSpace(t.Body)))
            throw new GeminiException(GeminiException.EmptyPrompt, "Prompt is empty.");
        if (GeminiCircuitBreaker.IsOpen)
            throw new GeminiException(GeminiException.Busy, "Gemini circuit is open.");

        var payload = BuildPayload(history, systemInstruction);
        var models = new List<string>();
        if (_workingModel is { } known) models.Add(known);
        models.Add(_settings.Model);
        models.AddRange(_settings.FallbackModels);

        GeminiException? last = null;
        foreach (var model in models.Distinct())
        {
            try
            {
                var reply = await SendWithRetryAsync(model, payload, ct);
                _workingModel = model;
                GeminiCircuitBreaker.RecordSuccess();
                return reply;
            }
            catch (GeminiException ex) when (ex.Code == GeminiException.Unavailable && ex.Message == "model-not-found")
            {
                // 404: probar el siguiente modelo.
                last = ex;
            }
            catch (GeminiException ex)
            {
                if (!accessory && ex.Code is GeminiException.Timeout or GeminiException.Unavailable)
                    GeminiCircuitBreaker.RecordFailure(_settings.CircuitBreakerFailures, _settings.CircuitBreakerOpenSeconds);
                throw;
            }
        }

        if (!accessory)
            GeminiCircuitBreaker.RecordFailure(_settings.CircuitBreakerFailures, _settings.CircuitBreakerOpenSeconds);
        throw new GeminiException(GeminiException.Unavailable, last?.Message ?? "No Gemini model available.");
    }

    private JsonObject BuildPayload(IReadOnlyList<GeminiTurn> history, string systemInstruction)
    {
        var contents = new JsonArray();
        foreach (var turn in history.Where(t => !string.IsNullOrWhiteSpace(t.Body)))
        {
            var isUser = turn.Role is "resident" or "user";
            contents.Add(new JsonObject
            {
                ["role"] = isUser ? "user" : "model",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = turn.Body }),
            });
        }

        return new JsonObject
        {
            ["contents"] = contents,
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemInstruction }),
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.2,
                ["maxOutputTokens"] = _settings.MaxOutputTokens,
            },
        };
    }

    // 1 reintento (~400 ms) ante 429/503.
    private async Task<string> SendWithRetryAsync(string model, JsonObject payload, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, body) = await PostAsync(model, payload, ct);
            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == 0)
                {
                    await Task.Delay(400, ct);
                    continue;
                }
                throw new GeminiException(status == HttpStatusCode.TooManyRequests ? GeminiException.Busy : GeminiException.Unavailable,
                    $"Gemini returned {(int)status}.");
            }
            if (status == HttpStatusCode.NotFound)
                throw new GeminiException(GeminiException.Unavailable, "model-not-found");
            if ((int)status >= 500)
                throw new GeminiException(GeminiException.Unavailable, $"Gemini returned {(int)status}.");
            if (!((int)status is >= 200 and < 300))
            {
                logger.LogWarning("Gemini {Model} rejected the request with {Status}.", model, (int)status);
                throw new GeminiException(GeminiException.RequestFailed, $"Gemini returned {(int)status}.");
            }
            return ExtractText(body);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string model, JsonObject payload, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 1, 120));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var url = $"{BaseUrl}{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(_settings.ApiKey)}";
            using var response = await http.PostAsJsonAsync(url, payload, cts.Token);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GeminiException(GeminiException.Timeout, "Gemini request timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Gemini request failed.");
            throw new GeminiException(GeminiException.RequestFailed, "Gemini request failed.");
        }
    }

    private static string ExtractText(string body)
    {
        try
        {
            var parts = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
            var text = string.Concat(parts?.Select(p => p?["text"]?.GetValue<string>()) ?? []);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch (JsonException)
        {
        }
        throw new GeminiException(GeminiException.EmptyReply, "Gemini returned an empty reply.");
    }
}
