using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
using BuildingFex.Api.Finances.Domain.Services;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Finances.Application.Internal.Billing;

/// <summary>Resultado de una corrida de facturación para un condominio.</summary>
public sealed record MonthlyBillingResult(
    int OwnerAdminId,
    string Month,
    int Created,
    int AlreadyBilled,
    int NotDue,
    bool Skipped,
    string? Reason);

/// <summary>Alerta de facturación para el admin (US021 "Ausencia de presupuesto base").</summary>
public sealed record BillingAlert(string Code, string Month, string Message);

public interface IMonthlyBillingService
{
    /// <summary>Genera (idempotente) los recibos Pending del mes para los residentes que lo deben.</summary>
    Task<MonthlyBillingResult> GenerateForTenantAsync(int ownerAdminId, string? month = null, CancellationToken ct = default);

    /// <summary>Corre la facturación del mes actual para todos los condominios (worker nocturno).</summary>
    Task<IReadOnlyList<MonthlyBillingResult>> GenerateForAllTenantsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<BillingAlert>> GetAlertsAsync(int ownerAdminId, CancellationToken ct = default);
}

/// <summary>
/// GenerateFeesCommandHandler (Tabla 62) — port server-side de financesStore.generateMonthlyReceipts / generateInitialReceipt:
/// mes contable = mes actual (hora Lima); monto, apertura y vencimiento salen de la cuota configurada del mes;
/// el residente debe el mes según su fecha de ingreso, billFromNextMonth y el rango de días de admisión.
/// Sin cuota configurada se detiene la emisión y se devuelve una alerta (no se inventan montos).
/// La unicidad (owner_admin_id, resident_external_id, monthly_key) — solo cuotas MONTHLY — garantiza cero duplicados (RNF011),
/// incluso si el worker y el endpoint TS003 corren a la vez.
/// </summary>
public class MonthlyBillingService(AppDbContext db, ILogger<MonthlyBillingService> logger) : IMonthlyBillingService
{
    public const string ReasonMissingCharge = "MISSING_MONTHLY_CHARGE";
    public const string ReasonInvalidMonth = "INVALID_MONTH";

    public async Task<MonthlyBillingResult> GenerateForTenantAsync(
        int ownerAdminId,
        string? month = null,
        CancellationToken ct = default)
    {
        var today = FinanceClock.Today();
        Period period;
        if (string.IsNullOrWhiteSpace(month))
            period = Period.FromDate(today);
        else if (!Period.TryParse(month, out period))
            return new MonthlyBillingResult(ownerAdminId, month.Trim(), 0, 0, 0, true, ReasonInvalidMonth);

        var setting = await LoadSettingAsync(ownerAdminId, ct);
        var charge = setting?.FindCharge(period);
        if (setting is null || charge is null || !charge.IsBillable)
        {
            logger.LogWarning(
                "Monthly billing skipped for admin {OwnerAdminId} ({Month}): no monthly charge configured.",
                ownerAdminId, period.Key);
            return new MonthlyBillingResult(ownerAdminId, period.Key, 0, 0, 0, true, ReasonMissingCharge);
        }

        var residents = await db.Set<User>()
            .AsNoTracking()
            .Where(u => u.Role == "resident" && u.OwnerAdminId == ownerAdminId)
            .ToListAsync(ct);

        var monthKey = period.Key;
        var existing = await db.Set<Receipt>()
            .AsNoTracking()
            .Where(r => r.OwnerAdminId == ownerAdminId)
            .Where(r => r.BillingMonth == monthKey || r.BillingMonth == null)
            .ToListAsync(ct);
        var billedResidents = existing
            .Where(r => r.CoversMonth(period))
            .Select(r => r.ResidentExternalId)
            .ToHashSet(StringComparer.Ordinal);

        int created = 0, alreadyBilled = 0, notDue = 0;

        foreach (var resident in residents)
        {
            if (!ResidentOwesMonth(resident, period, setting))
            {
                notDue++;
                continue;
            }
            if (billedResidents.Contains(resident.ExternalId))
            {
                alreadyBilled++;
                continue;
            }

            // Guid: los ids "{ms}{seq}" podían chocar entre condominios facturados en el mismo milisegundo.
            var externalId = NewReceiptExternalId();
            var receipt = Receipt.CreateMonthly(externalId, ownerAdminId, resident.ExternalId, charge, today);
            db.Add(receipt);
            try
            {
                await db.SaveChangesAsync(ct);
                created++;
            }
            catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex, ReceiptIndexes.UniqueMonthly))
            {
                // Otra corrida concurrente ya emitió este mes para el residente: el índice único gana.
                db.Entry(receipt).State = EntityState.Detached;
                alreadyBilled++;
                logger.LogInformation(
                    "Monthly billing: receipt for {Resident} / {Month} already exists (concurrent run).",
                    resident.ExternalId, period.Key);
            }
            catch (DbUpdateException)
            {
                // Cualquier otro error (FK, columna, conexión) no es "ya facturado": se propaga.
                db.Entry(receipt).State = EntityState.Detached;
                throw;
            }
        }

        if (created > 0)
            logger.LogInformation(
                "Monthly billing admin {OwnerAdminId} {Month}: {Created} created, {Already} already billed, {NotDue} not due.",
                ownerAdminId, period.Key, created, alreadyBilled, notDue);

        return new MonthlyBillingResult(ownerAdminId, period.Key, created, alreadyBilled, notDue, false, null);
    }

    public async Task<IReadOnlyList<MonthlyBillingResult>> GenerateForAllTenantsAsync(CancellationToken ct = default)
    {
        var adminIds = await db.Set<User>()
            .AsNoTracking()
            .Where(u => u.Role == "admin")
            .Select(u => u.Id)
            .ToListAsync(ct);

        var results = new List<MonthlyBillingResult>(adminIds.Count);
        foreach (var adminId in adminIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(await GenerateForTenantAsync(adminId, null, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Monthly billing failed for admin {OwnerAdminId}", adminId);
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<BillingAlert>> GetAlertsAsync(int ownerAdminId, CancellationToken ct = default)
    {
        var period = Period.FromDate(FinanceClock.Today());
        var setting = await LoadSettingAsync(ownerAdminId, ct);
        var charge = setting?.FindCharge(period);
        if (charge is not null && charge.IsBillable)
            return [];

        return
        [
            new BillingAlert(
                ReasonMissingCharge,
                period.Key,
                $"No se configuró la cuota de mantenimiento de {period.Key}. La facturación automática está detenida hasta que registres el monto."),
        ];
    }

    /// <summary>"rc-" + 32 hex (35 car.): cabe con holgura en receipts.external_id (64) y en payments "MP-{id}-R-{recibo}".</summary>
    public static string NewReceiptExternalId() => $"rc-{Guid.NewGuid():N}";

    private Task<FinanceSetting?> LoadSettingAsync(int ownerAdminId, CancellationToken ct) =>
        db.Set<FinanceSetting>()
            .AsNoTracking()
            .Where(s => s.OwnerAdminId == ownerAdminId)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Port de billingCycle.residentOwesMonth: se factura desde la fecha de ingreso registrada por el admin.
    /// Mes de ingreso: solo si no marcó "facturar desde el mes siguiente" y entró dentro del rango de días de admisión.
    /// </summary>
    public static bool ResidentOwesMonth(User resident, Period month, FinanceSetting? setting)
    {
        if (resident.AdmissionDate is not { } admission) return true;
        var admissionMonth = Period.FromDate(admission);
        var cmp = admissionMonth.CompareTo(month);
        if (cmp < 0) return true;
        if (cmp > 0) return false;
        if (resident.BillFromNextMonth) return false;
        var from = setting?.AdmissionPayFromDay ?? 1;
        var to = setting?.AdmissionPayToDay ?? 7;
        return admission.Day >= from && admission.Day <= to;
    }
}
