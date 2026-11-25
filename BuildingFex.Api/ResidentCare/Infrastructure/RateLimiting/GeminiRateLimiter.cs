using System.Collections.Concurrent;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.ResidentCare.Infrastructure.RateLimiting;

/// <summary>Rate limit in-memory por uid para llamadas Gemini.</summary>
public class GeminiRateLimiter(IOptions<GeminiSettings> options)
{
    private readonly ConcurrentDictionary<string, Queue<long>> _hits = new(StringComparer.Ordinal);

    public bool TryAcquire(string userKey)
    {
        var key = (userKey ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(key)) return false;

        var limit = Math.Max(1, options.Value.MaxRequestsPerMinute);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowStart = now - 60_000;

        var q = _hits.GetOrAdd(key, _ => new Queue<long>());
        lock (q)
        {
            while (q.Count > 0 && q.Peek() < windowStart)
                q.Dequeue();

            if (q.Count >= limit)
                return false;

            q.Enqueue(now);
            return true;
        }
    }
}
