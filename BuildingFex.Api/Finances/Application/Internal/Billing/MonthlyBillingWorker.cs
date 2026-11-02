namespace BuildingFex.Api.Finances.Application.Internal.Billing;

/// <summary>
/// Revisión automática nocturna (US021 / RNF011): una vez al día emite los recibos del mes actual
/// para todos los condominios. Es idempotente, así que reiniciar el servidor o correr varias instancias no duplica.
/// Config: Finances:MonthlyBilling:Enabled (true), IntervalHours (24), StartupDelaySeconds (60).
/// </summary>
public class MonthlyBillingWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<MonthlyBillingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("Finances:MonthlyBilling");
        if (!section.GetValue("Enabled", true))
        {
            logger.LogInformation("MonthlyBillingWorker disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, section.GetValue("IntervalHours", 24)));
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, section.GetValue("StartupDelaySeconds", 60)));

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
                var billing = scope.ServiceProvider.GetRequiredService<IMonthlyBillingService>();
                var results = await billing.GenerateForAllTenantsAsync(stoppingToken);
                var created = results.Sum(r => r.Created);
                var skipped = results.Count(r => r.Skipped);
                logger.LogInformation(
                    "MonthlyBillingWorker run: {Tenants} tenant(s), {Created} receipt(s) created, {Skipped} skipped (no monthly charge).",
                    results.Count, created, skipped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MonthlyBillingWorker run failed.");
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
