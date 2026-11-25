using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.ResidentCare.Application;

/// <summary>Reserva de una consulta del tope mensual; se devuelve si al final no se usó la IA.</summary>
public sealed record AiQuotaReservation(string OwnerAdminId, string Period, int Cap);

/// <summary>Reserva de una consulta del tope diario del residente (US045).</summary>
public sealed record ResidentAiReservation(string OwnerAdminId, string ResidentId, string Day, int Limit);

public sealed record AiUsageSnapshot(string Period, int Used, int Cap, int Filtered = 0)
{
    public int Remaining => Math.Max(0, Cap - Used);
    public bool CapReached => Used >= Cap;
}

/// <summary>
/// Tope mensual de consultas al asistente por cuenta (CON11). El incremento es atómico en MySQL
/// (UPDATE … WHERE query_count &lt; cap): dos residentes a la vez nunca superan el tope.
/// </summary>
public interface IAiUsageQuota
{
    /// <summary>Null si la cuenta ya alcanzó el tope del periodo.</summary>
    Task<AiQuotaReservation?> TryConsumeAsync(string ownerAdminId, int cap, CancellationToken ct = default);

    /// <summary>Devuelve una consulta reservada (la IA no llegó a responder).</summary>
    Task ReleaseAsync(AiQuotaReservation reservation, CancellationToken ct = default);

    Task<AiUsageSnapshot> GetAsync(string ownerAdminId, int cap, CancellationToken ct = default);

    /// <summary>US045: null si el residente ya llegó a su límite diario.</summary>
    Task<ResidentAiReservation?> TryConsumeResidentDailyAsync(string ownerAdminId, string residentId, int limit, CancellationToken ct = default);

    Task ReleaseResidentDailyAsync(ResidentAiReservation reservation, CancellationToken ct = default);

    /// <summary>US045: cuenta un mensaje respondido sin IA (métrica; no gasta tope).</summary>
    Task RecordFilteredAsync(string ownerAdminId, CancellationToken ct = default);
}

public class AiUsageQuota(AppDbContext db, ILogger<AiUsageQuota> logger) : IAiUsageQuota
{
    /// <summary>Periodo de facturación en hora de Lima (UTC-5, sin horario de verano).</summary>
    public static string CurrentPeriod(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(-5)).ToString("yyyy-MM");

    /// <summary>Día en hora de Lima (UTC-5), igual que <see cref="CurrentPeriod"/>.</summary>
    public static string CurrentDay(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(-5)).ToString("yyyy-MM-dd");

    public async Task<AiQuotaReservation?> TryConsumeAsync(string ownerAdminId, int cap, CancellationToken ct = default)
    {
        var owner = (ownerAdminId ?? string.Empty).Trim();
        if (owner.Length == 0 || cap <= 0) return null;
        var period = CurrentPeriod();
        var now = DateTime.UtcNow;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT IGNORE INTO ai_usages (owner_admin_id, period, query_count, created_at, updated_at)
            VALUES ({owner}, {period}, 0, {now}, {now})
            """, ct);

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ai_usages
            SET query_count = query_count + 1, updated_at = {now}
            WHERE owner_admin_id = {owner} AND period = {period} AND query_count < {cap}
            """, ct);

        return affected > 0 ? new AiQuotaReservation(owner, period, cap) : null;
    }

    public async Task ReleaseAsync(AiQuotaReservation reservation, CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ai_usages
                SET query_count = query_count - 1, updated_at = {now}
                WHERE owner_admin_id = {reservation.OwnerAdminId} AND period = {reservation.Period} AND query_count > 0
                """, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Si falla, la cuenta pierde una consulta del mes; la respuesta al residente no se interrumpe.
            logger.LogWarning(ex, "AI quota release failed for {Owner} {Period}", reservation.OwnerAdminId, reservation.Period);
        }
    }

    public async Task<AiUsageSnapshot> GetAsync(string ownerAdminId, int cap, CancellationToken ct = default)
    {
        var owner = (ownerAdminId ?? string.Empty).Trim();
        var period = CurrentPeriod();
        var row = await db.Set<Domain.Model.Aggregates.AiUsage>()
            .AsNoTracking()
            .Where(u => u.OwnerAdminId == owner && u.Period == period)
            .Select(u => new { u.QueryCount, u.FilteredCount })
            .FirstOrDefaultAsync(ct);
        var used = row?.QueryCount;
        var filtered = row?.FilteredCount;
        return new AiUsageSnapshot(period, used ?? 0, cap, filtered ?? 0);
    }

    public async Task<ResidentAiReservation?> TryConsumeResidentDailyAsync(
        string ownerAdminId, string residentId, int limit, CancellationToken ct = default)
    {
        var owner = (ownerAdminId ?? string.Empty).Trim();
        var resident = (residentId ?? string.Empty).Trim();
        if (owner.Length == 0 || resident.Length == 0 || limit <= 0) return null;
        var day = CurrentDay();
        var now = DateTime.UtcNow;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT IGNORE INTO resident_ai_usages (owner_admin_id, resident_id, `day`, query_count, created_at, updated_at)
            VALUES ({owner}, {resident}, {day}, 0, {now}, {now})
            """, ct);

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE resident_ai_usages
            SET query_count = query_count + 1, updated_at = {now}
            WHERE owner_admin_id = {owner} AND resident_id = {resident} AND `day` = {day} AND query_count < {limit}
            """, ct);

        return affected > 0 ? new ResidentAiReservation(owner, resident, day, limit) : null;
    }

    public async Task ReleaseResidentDailyAsync(ResidentAiReservation reservation, CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE resident_ai_usages
                SET query_count = query_count - 1, updated_at = {now}
                WHERE owner_admin_id = {reservation.OwnerAdminId} AND resident_id = {reservation.ResidentId}
                  AND `day` = {reservation.Day} AND query_count > 0
                """, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Si falla, el residente pierde una consulta del día; la respuesta no se interrumpe.
            logger.LogWarning(ex, "Resident daily AI release failed for {Resident} {Day}", reservation.ResidentId, reservation.Day);
        }
    }

    public async Task RecordFilteredAsync(string ownerAdminId, CancellationToken ct = default)
    {
        try
        {
            var owner = (ownerAdminId ?? string.Empty).Trim();
            if (owner.Length == 0) return;
            var period = CurrentPeriod();
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ai_usages (owner_admin_id, period, query_count, filtered_count, created_at, updated_at)
                VALUES ({owner}, {period}, 0, 1, {now}, {now})
                ON DUPLICATE KEY UPDATE filtered_count = filtered_count + 1, updated_at = {now}
                """, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Es una métrica: si falla no se interrumpe la respuesta.
            logger.LogWarning(ex, "AI filtered counter failed for {Owner}", ownerAdminId);
        }
    }
}
