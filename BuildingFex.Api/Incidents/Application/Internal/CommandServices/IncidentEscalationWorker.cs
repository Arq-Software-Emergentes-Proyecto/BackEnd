namespace BuildingFex.Api.Incidents.Application.Internal.CommandServices;

/// <summary>
/// Ejecuta <see cref="IncidentEscalationJob"/> fuera de Lambda (desarrollo / contenedor).
/// Config: Incidents:Escalation:Enabled (true), IntervalMinutes (60), StartupDelaySeconds (90).
/// </summary>
public class IncidentEscalationWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<IncidentEscalationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("Incidents:Escalation");
        if (!section.GetValue("Enabled", true))
        {
            logger.LogInformation("IncidentEscalationWorker disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, section.GetValue("IntervalMinutes", 60)));
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, section.GetValue("StartupDelaySeconds", 90)));

        try
        {
            await Task.Delay(startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IncidentEscalationJob>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "IncidentEscalationWorker run failed.");
            }
        }
        while (await WaitNextAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
