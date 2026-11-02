using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Services;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Finances.Application.Internal.Delinquency;

/// <summary>Estado de cuenta resumido de un residente, para otros contextos (Social Spaces).</summary>
public sealed record AccountStatus(bool IsDelinquent, int MaxDaysOverdue, decimal OverdueAmount, bool RestrictsReservations);

public interface IAccountStatusQueryService
{
    Task<AccountStatus> GetAsync(int ownerAdminId, string residentExternalId, CancellationToken ct = default);
}

/// <summary>
/// Consulta EstadoDeCuenta (Finances → Social Spaces): moroso según <see cref="DelinquencyPolicy"/> y si el
/// reglamento del edificio (ajuste de finanzas) restringe las reservas a morosos. Se calcula al consultar,
/// así que pagar la cuota levanta la restricción de inmediato.
/// </summary>
public class AccountStatusQueryService(AppDbContext db) : IAccountStatusQueryService
{
    private sealed record Item(string? Status, string? DueDate, decimal Amount);

    public async Task<AccountStatus> GetAsync(int ownerAdminId, string residentExternalId, CancellationToken ct = default)
    {
        var resident = (residentExternalId ?? string.Empty).Trim();
        var today = FinanceClock.Today();

        var receipts = await db.Set<Receipt>().AsNoTracking()
            .Where(r => r.OwnerAdminId == ownerAdminId && r.ResidentExternalId == resident)
            .Select(r => new Item(r.Status, r.DueDate, r.Amount + r.LateFee + r.ExtraCharges))
            .ToListAsync(ct);
        var fees = await db.Set<Fee>().AsNoTracking()
            .Where(f => f.OwnerAdminId == ownerAdminId && f.ResidentExternalId == resident)
            .Select(f => new Item(f.Status, f.DueDate, f.Amount))
            .ToListAsync(ct);

        var items = receipts.Concat(fees).ToList();
        var maxDays = items.Select(i => DelinquencyPolicy.DaysOverdue(i.Status, i.DueDate, today)).DefaultIfEmpty(0).Max();
        var overdue = items.Where(i => DelinquencyPolicy.IsOverdue(i.Status, i.DueDate, today)).Sum(i => i.Amount);
        var delinquent = items.Any(i => DelinquencyPolicy.IsDelinquent(i.Status, i.DueDate, today));

        var restricts = await db.Set<FinanceSetting>().AsNoTracking()
            .Where(s => s.OwnerAdminId == ownerAdminId)
            .Select(s => s.RestrictReservationsWhenDelinquent)
            .FirstOrDefaultAsync(ct);

        return new AccountStatus(delinquent, maxDays, overdue, restricts && delinquent);
    }
}
