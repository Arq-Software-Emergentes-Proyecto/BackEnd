namespace BuildingFex.Api.ResidentCare.Application;

/// <summary>
/// Indexa documentos de Contexto IA en background (no bloquea el POST de upload).
/// En AWS Lambda no se registra: ahí lo hace la función de jobs (ver LambdaJobs).
/// </summary>
public class AiContextIndexingWorker(
    IServiceScopeFactory scopeFactory,
    IAiContextIndexingQueue queue,
    ILogger<AiContextIndexingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueueStaleEmbeddingsAsync(stoppingToken);

        await foreach (var uploadId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AiContextIndexingJob>().ProcessAsync(uploadId, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI context indexing failed for {UploadId}", uploadId);
            }
        }
    }

    /// <summary>
    /// Al arrancar: documentos vigentes cuyos chunks se generaron con otro modelo de embeddings (p. ej.
    /// text-embedding-004 → gemini-embedding-001) se re-indexan con el actual. Sin API key de Gemini no se encola
    /// nada (no se podría vectorizar; mientras tanto esos chunks se buscan por palabras clave).
    /// </summary>
    private async Task EnqueueStaleEmbeddingsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var stale = await scope.ServiceProvider.GetRequiredService<AiContextIndexingJob>()
                .FindWorkAsync(includePending: false, ct);
            foreach (var uploadId in stale)
                await queue.EnqueueAsync(uploadId, ct);
            if (stale.Count > 0)
                logger.LogInformation("AI context: {Count} document(s) queued for re-index", stale.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AI context: stale embedding scan skipped");
        }
    }
}
