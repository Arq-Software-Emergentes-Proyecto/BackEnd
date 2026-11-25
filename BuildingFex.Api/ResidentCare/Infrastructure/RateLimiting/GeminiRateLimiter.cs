using System.Collections.Concurrent;
using BuildingFex.Api.ResidentCare.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.ResidentCare.Infrastructure.RateLimiting;

/// <summary>
/// TS010: límite de llamadas a Gemini por usuario (uid) en una ventana móvil de 60 s. Va como singleton:
/// en Lambda cada instancia cuenta por su lado, lo que basta para frenar ráfagas de un mismo residente.
/// </summary>
public class GeminiRateLimiter(IOptions<GeminiSettings> options)
{
    private const long WindowMs = 60_000;

    private readonly ConcurrentDictionary<string, Queue<long>> _calls = new(StringComparer.Ordinal);
    private readonly int _limit = Math.Max(1, options.Value.MaxRequestsPerMinute);

    /// <summary>True si el usuario aún tiene cupo en el último minuto; registra la llamada.</summary>
    public bool TryAcquire(string userKey)
    {
        // Sin uid no hay a quién atribuir la llamada: no se deja pasar.
        var key = (userKey ?? string.Empty).Trim();
        if (key.Length == 0) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var queue = _calls.GetOrAdd(key, _ => new Queue<long>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > WindowMs)
                queue.Dequeue();

            if (queue.Count >= _limit) return false;

            queue.Enqueue(now);
            return true;
        }
    }
}
