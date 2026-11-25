using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using BuildingFex.Api.ResidentCare.Application;
using BuildingFex.Api.Shared.Infrastructure.Hosting;

namespace BuildingFex.Api.ResidentCare.Infrastructure.Lambda;

/// <summary>
/// En AWS Lambda la indexación no puede seguir en background tras responder: se invoca la función de jobs
/// en modo asíncrono por cada documento. Si la invocación falla, el barrido programado lo recoge (sigue "pending").
/// </summary>
public class LambdaAiContextIndexingQueue(
    IAmazonLambda lambda,
    ILogger<LambdaAiContextIndexingQueue> logger) : IAiContextIndexingQueue
{
    public async ValueTask EnqueueAsync(string uploadExternalId, CancellationToken ct = default)
    {
        var id = (uploadExternalId ?? string.Empty).Trim();
        var functionName = System.Environment.GetEnvironmentVariable(LambdaHosting.JobsFunctionVariable);
        if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(functionName)) return;

        try
        {
            await lambda.InvokeAsync(new InvokeRequest
            {
                FunctionName = functionName,
                InvocationType = InvocationType.Event,
                Payload = JsonSerializer.Serialize(new LambdaJobRequest("ai-index", id)),
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not start indexing for {UploadId}; the scheduled sweep will retry", id);
        }
    }

    public async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
