using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using BuildingFex.Api.Communications.Infrastructure;
using BuildingFex.Api.Finances.Application.Internal.Billing;

namespace BuildingFex.Api.Shared.Infrastructure.Hosting;

/// <summary>Evento de entrada para invocar un job periódico en Lambda.</summary>
public sealed record LambdaJobRequest(
    [property: JsonPropertyName("job")] string? Job);

/// <summary>Resultado de la ejecución de un job periódico en Lambda.</summary>
public sealed record LambdaJobResult(
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("processed")] int Processed);

/// <summary>
/// Punto de entrada para la ejecución de procesos periódicos en AWS Lambda (JobsFunction).
/// Reemplaza a los BackgroundService en entornos serverless:
/// - "migrate": aplica esquema y seed tras cada despliegue.
/// - "monthly-billing": generación automática de recibos diarios (06:15 Lima).
/// - "import-cleanup": limpieza periódica de cargas huérfanas cada 30 minutos.
/// </summary>
public static class LambdaJobs
{
    public static async Task RunAsync(WebApplication app)
    {
        var handler = (LambdaJobRequest request, ILambdaContext context) => ExecuteAsync(app, request, context);
        await LambdaBootstrapBuilder.Create(handler, new DefaultLambdaJsonSerializer())
            .Build()
            .RunAsync();
    }

    private static async Task<LambdaJobResult> ExecuteAsync(
        WebApplication app,
        LambdaJobRequest? request,
        ILambdaContext context)
    {
        var remaining = context.RemainingTime - TimeSpan.FromSeconds(15);
        var timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
        using var cts = new CancellationTokenSource(timeout);
        var ct = cts.Token;

        var job = request?.Job?.Trim().ToLowerInvariant() ?? string.Empty;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LambdaJobs");
        logger.LogInformation("Lambda job {Job} started", job);

        switch (job)
        {
            case "migrate":
            {
                await DatabaseBootstrap.RunAsync(app.Services, app.Configuration, app.Environment.ContentRootPath);
                return new LambdaJobResult(job, 1);
            }
            case "monthly-billing":
            {
                await using var scope = app.Services.CreateAsyncScope();
                var billingService = scope.ServiceProvider.GetRequiredService<IMonthlyBillingService>();
                var results = await billingService.GenerateForAllTenantsAsync(ct);
                var processed = results.Sum(r => r.Created);
                logger.LogInformation(
                    "Lambda job {Job} completed: {Tenants} tenant(s), {Created} receipt(s) created",
                    job, results.Count, processed);
                return new LambdaJobResult(job, processed);
            }
            case "import-cleanup":
            {
                await using var scope = app.Services.CreateAsyncScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<ImportOrphanCleanup>();
                var processed = await cleanup.RunOnceAsync(ct);
                logger.LogInformation(
                    "Lambda job {Job} completed: {Deleted} orphan upload(s) deleted",
                    job, processed);
                return new LambdaJobResult(job, processed);
            }
            default:
                throw new ArgumentException($"Unknown job '{request?.Job}'.");
        }
    }
}
