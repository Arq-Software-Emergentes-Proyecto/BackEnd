namespace BuildingFex.Api.ResidentCare.Application;

/// <summary>Worker en segundo plano (solo fuera de Lambda) que indexa documentos de Contexto IA.</summary>
public class AiContextIndexingWorker(
    IServiceScopeFactory scopes,
    IAiContextIndexingQueue queue,
    ILogger<AiContextIndexingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var job = scope.ServiceProvider.GetRequiredService<AiContextIndexingJob>();
            var staleIds = await job.FindWorkAsync(includePending: false, stoppingToken);
            foreach (var id in staleIds)
                await queue.EnqueueAsync(id, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AiContextIndexingWorker startup scan failed");
        }

        await foreach (var uploadId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var job = scope.ServiceProvider.GetRequiredService<AiContextIndexingJob>();
                await job.ProcessAsync(uploadId, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AiContextIndexingWorker failed for {UploadId}", uploadId);
            }
        }
    }
}
