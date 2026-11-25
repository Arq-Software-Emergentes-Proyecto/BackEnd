using System.Threading.Channels;

namespace BuildingFex.Api.ResidentCare.Application;

public interface IAiContextIndexingQueue
{
    ValueTask EnqueueAsync(string uploadExternalId, CancellationToken ct = default);
    IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct);
}

/// <summary>Cola en memoria (fuera de Lambda) para indexar documentos de Contexto IA.</summary>
public class AiContextIndexingQueue : IAiContextIndexingQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(string uploadExternalId, CancellationToken ct = default)
    {
        var clean = (uploadExternalId ?? string.Empty).Trim();
        return clean.Length == 0 ? ValueTask.CompletedTask : _channel.Writer.WriteAsync(clean, ct);
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
