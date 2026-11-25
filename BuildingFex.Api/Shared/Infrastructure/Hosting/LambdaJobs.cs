using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using BuildingFex.Api.Communications.Infrastructure;
using BuildingFex.Api.Finances.Application.Internal.Billing;
using BuildingFex.Api.Incidents.Application.Internal.CommandServices;
using BuildingFex.Api.ResidentCare.Application;

namespace BuildingFex.Api.Shared.Infrastructure.Hosting;

/// <summary>Evento de la función de jobs: <c>{ "job": "monthly-billing" }</c> o <c>{ "job": "ai-index", "uploadId": "…" }</c>.</summary>
public sealed record LambdaJobRequest(
    [property: JsonPropertyName("job")] string? Job,
    [property: JsonPropertyName("uploadId")] string? UploadId);

public sealed record LambdaJobResult(
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("processed")] int Processed);

/// <summary>
/// Reemplaza a los BackgroundService en AWS Lambda (allí el proceso se congela entre invocaciones):
/// migrate (tras desplegar), monthly-billing (diario), import-cleanup (cada 30 min),
/// incident-escalation (cada hora) y ai-index
/// (por documento, asíncrono desde la API, más un barrido programado de pendientes).
/// </summary>
public static class LambdaJobs
{
    public static Task RunAsync(WebApplication app)
    {
        var handler = (LambdaJobRequest request, ILambdaContext context) => ExecuteAsync(app, request, context);
        return LambdaBootstrapBuilder.Create(handler, new DefaultLambdaJsonSerializer()).Build().RunAsync();
    }

    private static async Task<LambdaJobResult> ExecuteAsync(WebApplication app, LambdaJobRequest request, ILambdaContext context)
    {
        // Deja margen para registrar el resultado antes de que Lambda corte la invocación.
        using var cts = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(15));
        var ct = cts.Token;
        var job = (request.Job ?? string.Empty).Trim().ToLowerInvariant();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LambdaJobs");
        logger.LogInformation("Lambda job {Job} started", job);

        switch (job)
        {
            case "migrate":
                await DatabaseBootstrap.RunAsync(app.Services, app.Configuration, app.Environment.ContentRootPath);
                return new LambdaJobResult(job, 1);

            case "monthly-billing":
            {
                await using var scope = app.Services.CreateAsyncScope();
                var results = await scope.ServiceProvider.GetRequiredService<IMonthlyBillingService>()
                    .GenerateForAllTenantsAsync(ct);
                var created = results.Sum(r => r.Created);
                logger.LogInformation("Monthly billing: {Tenants} tenant(s), {Created} receipt(s) created", results.Count, created);
                return new LambdaJobResult(job, created);
            }

            case "import-cleanup":
            {
                await using var scope = app.Services.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<ImportOrphanCleanup>().RunOnceAsync(ct);
                return new LambdaJobResult(job, removed);
            }

            case "incident-escalation":
            {
                await using var scope = app.Services.CreateAsyncScope();
                var escalated = await scope.ServiceProvider.GetRequiredService<IncidentEscalationJob>().RunOnceAsync(ct);
                return new LambdaJobResult(job, escalated);
            }

            case "ai-index":
                return new LambdaJobResult(job, await RunIndexingAsync(app.Services, request.UploadId, logger, ct));

            default:
                throw new ArgumentException($"Unknown job '{request.Job}'.");
        }
    }

    private static async Task<int> RunIndexingAsync(IServiceProvider services, string? uploadId, ILogger logger, CancellationToken ct)
    {
        IReadOnlyList<string> ids;
        if (!string.IsNullOrWhiteSpace(uploadId))
        {
            ids = [uploadId.Trim()];
        }
        else
        {
            await using var scope = services.CreateAsyncScope();
            ids = await scope.ServiceProvider.GetRequiredService<AiContextIndexingJob>().FindWorkAsync(includePending: true, ct);
        }

        var processed = 0;
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AiContextIndexingJob>().ProcessAsync(id, ct);
                processed++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "AI context indexing failed for {UploadId}", id);
            }
        }
        return processed;
    }
}
