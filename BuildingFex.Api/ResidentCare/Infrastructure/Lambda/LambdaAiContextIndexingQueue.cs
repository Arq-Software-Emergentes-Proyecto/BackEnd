using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using BuildingFex.Api.ResidentCare.Application;
using BuildingFex.Api.Shared.Infrastructure.Hosting;

namespace BuildingFex.Api.ResidentCare.Infrastructure.Lambda;

/// <summary>Cola de indexación IA en AWS: dispara JobsFunction de forma asíncrona (InvocationType.Event).</summary>
public class LambdaAiContextIndexingQueue(
    IAmazonLambda lambda,
    ILogger<LambdaAiContextIndexingQueue> logger) : IAiContextIndexingQueue
{
    public async ValueTask EnqueueAsync(string uploadExternalId, CancellationToken ct = default)
    {
        var clean = (uploadExternalId ?? string.Empty).Trim();
        var functionName = System.Environment.GetEnvironmentVariable(LambdaHosting.JobsFunctionVariable)?.Trim();
        if (string.IsNullOrEmpty(clean) || string.IsNullOrEmpty(functionName))
            return;

        try
        {
            var payload = JsonSerializer.Serialize(new LambdaJobRequest("ai-index", clean));
            await lambda.InvokeAsync(new InvokeRequest
            {
                FunctionName = functionName,
                InvocationType = InvocationType.Event,
                Payload = payload,
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Si la invocación falla, el barrido programado lo recoge porque sigue en "pending".
            logger.LogWarning(ex, "Async Lambda invoke for ai-index failed ({UploadId})", clean);
        }
    }

    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
