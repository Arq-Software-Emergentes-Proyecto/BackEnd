using System.Text;
using System.Text.Json;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.ResidentCare.Infrastructure.Gemini;

public sealed record GeminiTurn(string Role, string Body);

public interface IGeminiClient
{
    Task<string> GenerateReplyAsync(
        IReadOnlyList<GeminiTurn> history,
        string systemInstruction,
        CancellationToken ct = default);

    Task<string> SummarizeTitleAsync(
        IReadOnlyList<GeminiTurn> history,
        string locale,
        CancellationToken ct = default);
}

/// <summary>
/// Adaptador (ACL) hacia Gemini. QAS05: timeout corto por llamada, un reintento con backoff ante
/// saturación y circuit breaker; todo error sale como <see cref="GeminiException"/> con un código estable.
/// </summary>
public class GeminiClient(
    HttpClient http,
    IOptions<GeminiSettings> options,
    ILogger<GeminiClient> logger) : IGeminiClient
{
    private static string? _resolvedModel;

    public Task<string> GenerateReplyAsync(
        IReadOnlyList<GeminiTurn> history,
        string systemInstruction,
        CancellationToken ct = default) =>
        GenerateAsync(history, systemInstruction, countsForBreaker: true, ct);

    /// <param name="countsForBreaker">
    /// false para llamadas accesorias (título del chat): sus fallos no abren el circuit breaker de las respuestas
    /// al residente, y no se intentan mientras el circuito esté abierto o con fallos recientes.
    /// </param>
    private async Task<string> GenerateAsync(
        IReadOnlyList<GeminiTurn> history,
        string systemInstruction,
        bool countsForBreaker,
        CancellationToken ct)
    {
        var settings = options.Value;
        var apiKey = settings.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
            throw new GeminiException("GEMINI_API_KEY_MISSING", "Gemini API key is not configured on the server.");

        var contents = ToContents(history);
        if (contents.Count == 0)
            throw new GeminiException("GEMINI_EMPTY_PROMPT", "Message is required.");

        if (GeminiCircuitBreaker.IsOpen(out var retryAfter))
            throw new GeminiException("GEMINI_UNAVAILABLE", $"Gemini circuit open; retry in {retryAfter.TotalSeconds:F0}s.");
        if (!countsForBreaker && GeminiCircuitBreaker.HasRecentFailures)
            throw new GeminiException("GEMINI_UNAVAILABLE", "Gemini is degraded; optional call skipped.");

        void RecordFailure()
        {
            if (countsForBreaker) GeminiCircuitBreaker.RecordFailure(settings);
        }

        var candidates = BuildModelCandidates();
        string? lastMessage = null;
        var lastBusy = false;
        var busyAttempts = 0;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 120));
        var maxTokens = Math.Clamp(settings.MaxOutputTokens, 64, 8192);

        foreach (var modelId in candidates)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                (bool Ok, int Status, JsonElement Payload, string? ErrorMessage) result;
                try
                {
                    result = await RequestAsync(apiKey, modelId, contents, systemInstruction, 0.2, maxTokens, timeout, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Gemini {Model} timed out after {Timeout}s", modelId, timeout.TotalSeconds);
                    RecordFailure();
                    throw new GeminiException("GEMINI_TIMEOUT", "Gemini did not answer in time.");
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning(ex, "Gemini {Model} network error", modelId);
                    RecordFailure();
                    throw new GeminiException("GEMINI_UNAVAILABLE", "Gemini is unreachable.");
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Gemini {Model} returned invalid JSON", modelId);
                    RecordFailure();
                    throw new GeminiException("GEMINI_REQUEST_FAILED", "Gemini returned an invalid response.");
                }

                var (ok, status, payload, errorMessage) = result;
                if (ok)
                {
                    var text = ReadReply(payload);
                    if (string.IsNullOrWhiteSpace(text))
                        throw new GeminiException("GEMINI_EMPTY_REPLY", "Gemini returned an empty reply.");
                    _resolvedModel = modelId;
                    GeminiCircuitBreaker.RecordSuccess();
                    return text;
                }

                lastMessage = errorMessage;
                if (IsUnavailable(status, errorMessage)) break;
                if (IsBusy(status, errorMessage))
                {
                    lastBusy = true;
                    // Presupuesto de reintentos (QAS05): como máximo 3 llamadas saturadas en total.
                    if (++busyAttempts >= 3) goto Exhausted;
                    if (string.Equals(_resolvedModel, modelId, StringComparison.OrdinalIgnoreCase))
                        _resolvedModel = null;
                    if (attempt == 0)
                    {
                        await Task.Delay(400, ct);
                        continue;
                    }

                    break;
                }

                if (status >= 500) RecordFailure();
                throw new GeminiException("GEMINI_REQUEST_FAILED", errorMessage ?? $"HTTP {status}");
            }
        }

        Exhausted:
        RecordFailure();
        throw new GeminiException(
            lastBusy ? "GEMINI_BUSY" : "GEMINI_REQUEST_FAILED",
            lastMessage ?? "Gemini request failed.");
    }

    public async Task<string> SummarizeTitleAsync(
        IReadOnlyList<GeminiTurn> history,
        string locale,
        CancellationToken ct = default)
    {
        var snippet = string.Join(
            '\n',
            history
                .Where(m => !string.IsNullOrWhiteSpace(m.Body))
                .Take(8)
                .Select(m =>
                    $"{(m.Role is "user" or "resident" ? "Resident" : "Assistant")}: {m.Body.Trim()}"));

        if (string.IsNullOrWhiteSpace(snippet))
            return string.Empty;

        var language = string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? "English" : "Spanish";
        var text = await GenerateAsync(
            [new GeminiTurn("user",
                $"Write a 3-6 word title summarizing this resident support chat. Language: {language}. Title only, no quotes.\n\n{snippet}")],
            "You only output a short chat title. No extra text.",
            countsForBreaker: false,
            ct);

        return CleanTitle(text);
    }

    private List<string> BuildModelCandidates()
    {
        var settings = options.Value;
        var list = new List<string>();
        void Add(string? id)
        {
            var clean = (id ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(clean)) return;
            if (list.Contains(clean, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(clean);
        }

        Add(_resolvedModel);
        Add(settings.Model);
        foreach (var m in settings.FallbackModels ?? []) Add(m);
        return list;
    }

    private async Task<(bool Ok, int Status, JsonElement Payload, string? ErrorMessage)> RequestAsync(
        string apiKey,
        string modelId,
        List<object> contents,
        string systemInstruction,
        double temperature,
        int maxTokens,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        ct = cts.Token;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(modelId)}:generateContent?key={Uri.EscapeDataString(apiKey)}");

        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                systemInstruction = new { parts = new[] { new { text = systemInstruction } } },
                contents,
                generationConfig = new { temperature, maxOutputTokens = maxTokens },
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await http.SendAsync(request, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement.Clone();

        if (response.IsSuccessStatusCode)
            return (true, (int)response.StatusCode, root, null);

        var message = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg)
            ? msg.GetString()
            : $"HTTP {(int)response.StatusCode}";

        logger.LogWarning("Gemini {Model} failed: {Status} {Message}", modelId, (int)response.StatusCode, message);
        return (false, (int)response.StatusCode, root, message);
    }

    private static List<object> ToContents(IReadOnlyList<GeminiTurn> history)
    {
        var contents = history
            .Where(m => !string.IsNullOrWhiteSpace(m.Body))
            .Select(m => (object)new
            {
                role = m.Role is "user" or "resident" ? "user" : "model",
                parts = new[] { new { text = m.Body.Trim() } },
            })
            .ToList();

        if (contents.Count > 0)
        {
            var first = JsonSerializer.Serialize(contents[0]);
            if (first.Contains("\"role\":\"model\""))
            {
                contents.Insert(0, new
                {
                    role = "user",
                    parts = new[] { new { text = "Hola" } },
                });
            }
        }

        return contents;
    }

    private static string ReadReply(JsonElement payload)
    {
        if (!payload.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return string.Empty;

        var first = candidates[0];
        if (!first.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        }

        return sb.ToString().Trim();
    }

    private static bool IsUnavailable(int status, string? message)
    {
        var text = (message ?? string.Empty).ToLowerInvariant();
        return status == 404
               || text.Contains("not found")
               || text.Contains("no longer available")
               || text.Contains("not supported for generatecontent");
    }

    private static bool IsBusy(int status, string? message)
    {
        var text = (message ?? string.Empty).ToLowerInvariant();
        return status is 429 or 503
               || text.Contains("high demand")
               || text.Contains("unavailable")
               || text.Contains("resource exhausted")
               || text.Contains("try again later");
    }

    private static string CleanTitle(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        if (string.IsNullOrEmpty(line)) return string.Empty;
        return line
            .Trim('"', '\'', '«', '»')
            .TrimEnd('.', '!', '?')
            .Trim()
            .SliceSafe(48);
    }
}

file static class StringSliceExt
{
    public static string SliceSafe(this string value, int max)
        => value.Length <= max ? value : value[..max];
}

public class GeminiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>Fallas del proveedor (no de configuración) que el cliente puede reintentar.</summary>
    public bool IsTransient => Code is "GEMINI_TIMEOUT" or "GEMINI_UNAVAILABLE" or "GEMINI_BUSY" or "GEMINI_REQUEST_FAILED" or "GEMINI_EMPTY_REPLY";
}

/// <summary>Circuit breaker de proceso para Gemini (QAS05: sin caídas en cascada).</summary>
public static class GeminiCircuitBreaker
{
    private static readonly object Gate = new();
    private static int _failures;
    private static DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public static bool IsOpen(out TimeSpan retryAfter)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            retryAfter = _openUntil > now ? _openUntil - now : TimeSpan.Zero;
            return _openUntil > now;
        }
    }

    /// <summary>Hay fallos acumulados sin un éxito posterior (el circuito podría abrirse pronto).</summary>
    public static bool HasRecentFailures
    {
        get
        {
            lock (Gate) return _failures > 0;
        }
    }

    public static void RecordFailure(GeminiSettings settings)
    {
        lock (Gate)
        {
            _failures++;
            if (_failures >= Math.Max(1, settings.CircuitBreakerFailures))
            {
                _openUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, settings.CircuitBreakerOpenSeconds));
                _failures = 0;
            }
        }
    }

    public static void RecordSuccess()
    {
        lock (Gate)
        {
            _failures = 0;
            _openUntil = DateTimeOffset.MinValue;
        }
    }
}
