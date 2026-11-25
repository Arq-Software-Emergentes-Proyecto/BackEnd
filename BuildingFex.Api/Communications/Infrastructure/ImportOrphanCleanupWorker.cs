using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Infrastructure.Configuration;
using BuildingFex.Api.Communications.Infrastructure.Storage;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.Communications.Infrastructure;

/// <summary>
/// Borra pending_upload huérfanos (fila + objeto Storage) tras OrphanTtlHours. Una pasada; la llaman el
/// worker (servidor normal, cada 30 min) y la función de jobs programada en AWS Lambda.
/// </summary>
public class ImportOrphanCleanup(
    AppDbContext db,
    IFirebaseStorageService storage,
    IOptions<ImportSettings> options,
    ILogger<ImportOrphanCleanup> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var ttlHours = Math.Max(1, options.Value.OrphanTtlHours);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-ttlHours);

        var orphans = await db.Set<ImportUpload>()
            .Where(u => u.UploadStatus == ImportUploadStatus.PendingUpload && u.UploadedAt < cutoff)
            .Take(100)
            .ToListAsync(ct);

        if (orphans.Count == 0) return 0;

        foreach (var orphan in orphans)
        {
            if (!string.IsNullOrWhiteSpace(orphan.StoragePath) && storage.IsConfigured)
            {
                try { await storage.DeleteAsync(orphan.StoragePath, ct); }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not delete orphan object {Path}", orphan.StoragePath);
                }
            }

            db.Set<ImportUpload>().Remove(orphan);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Import orphan cleanup removed {Count} pending uploads", orphans.Count);
        return orphans.Count;
    }
}

/// <summary>Corre <see cref="ImportOrphanCleanup"/> cada 30 min (no se registra en AWS Lambda).</summary>
public class ImportOrphanCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ImportOrphanCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Arranque suave + ciclo cada 30 min
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ImportOrphanCleanup>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Import orphan cleanup failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }
}
