using System.Threading.Channels;

namespace BuildingFex.Api.ResidentCare.Application;

public interface IAiContextIndexingQueue
{
    ValueTask EnqueueAsync(string uploadExternalId, CancellationToken ct = default);
    IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct);
}

public class AiContextIndexingQueue : IAiContextIndexingQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ValueTask EnqueueAsync(string uploadExternalId, CancellationToken ct = default)
    {
        var id = (uploadExternalId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id)) return ValueTask.CompletedTask;
        return _channel.Writer.WriteAsync(id, ct);
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
