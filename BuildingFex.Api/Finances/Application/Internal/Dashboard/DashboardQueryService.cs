using BuildingFex.Api.Finances.Application.Internal.Billing;
using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
using BuildingFex.Api.Finances.Domain.Services;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Finances.Application.Internal.Dashboard;

public class DashboardQueryService(
    AppDbContext dbContext,
    IMonthlyBillingService billingService) : IDashboardQueryService
{
    public async Task<DashboardKpiResponse> GetKpisAsync(int ownerAdminId, CancellationToken ct = default)
    {
        // Mes contable en hora Lima (FinanceClock), igual que vencimientos y facturación.
        var monthStart = FinanceClock.MonthStart();
        // Prefiltro en SQL holgado (un día antes: instantes con offsets negativos); el corte exacto se hace en
        // memoria con la fecha local del pago.
        var lowerBoundIso = monthStart.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var monthPayments = await dbContext.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.OwnerAdminId == ownerAdminId)
            .Where(p => p.PaidAt != null && p.PaidAt.CompareTo(lowerBoundIso) >= 0)
            .Select(p => new { p.PaidAt, p.Amount })
            .ToListAsync(ct);
        var totalCollected = monthPayments
            .Where(p => FinanceClock.TryParseLocalDate(p.PaidAt, out var paidOn) && paidOn >= monthStart)
            .Sum(p => p.Amount);

        var residentsQuery = dbContext.Set<User>()
            .Where(u => u.Role == "resident" && u.OwnerAdminId == ownerAdminId);

        var totalResidents = await residentsQuery.CountAsync(ct);
        var occupiedUnits = await residentsQuery
            .Where(u => u.Code != null && u.Code != string.Empty)
            .CountAsync(ct);

        var kpiRecord = await dbContext.Set<KpiRecord>()
            .Where(k => k.OwnerAdminId == ownerAdminId)
            .OrderByDescending(k => k.Id)
            .FirstOrDefaultAsync(ct);

        // Total units: the registered building(s) (TS001 department count) are the source of truth;
        // the legacy kpi record (seeded 0/0 at admin registration) is only a fallback.
        var registeredUnits = await dbContext.Set<BuildingFex.Api.Profiles.Domain.Model.Aggregates.Building>()
            .Where(b => b.OwnerAdminId == ownerAdminId)
            .SumAsync(b => (int?)b.DepartmentCount, ct) ?? 0;
        var baselineTotalUnits = registeredUnits > 0
            ? registeredUnits
            : (kpiRecord?.OccupiedUnits ?? 0) + (kpiRecord?.EmptyUnits ?? 0);
        var emptyUnits = baselineTotalUnits > occupiedUnits
            ? baselineTotalUnits - occupiedUnits
            : 0;

        var debt = await LoadDebtItemsAsync(ownerAdminId, ct);
        var totalPendingDebt = debt.Where(d => !d.Paid).Sum(d => d.Amount);
        var delinquency = await BuildDelinquencyAsync(ownerAdminId, debt, ct);

        var recentPayments = await dbContext.Set<Payment>()
            .Where(p => p.OwnerAdminId == ownerAdminId)
            .OrderByDescending(p => p.Id)
            .Take(10)
            .Select(p => new RecentPaymentDto(
                p.ExternalId,
                p.ResidentExternalId,
                p.Amount,
                p.PaidAt,
                p.Method,
                p.Reference))
            .ToListAsync(ct);

        var adminExpenses = await dbContext.Set<AdminManagementExpense>()
            .Where(e => e.OwnerAdminId == ownerAdminId)
            .OrderByDescending(e => e.PurchaseDate)
            .Take(20)
            .Select(e => new AdminExpenseSummaryDto(e.Name, e.Amount, e.PurchaseDate))
            .ToListAsync(ct);

        var totalAdminExpenses = adminExpenses.Sum(e => e.Amount);

        var monthKeys = Enumerable.Range(0, 6)
            .Select(i => monthStart.AddMonths(i - 5).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        var monthlyChart = await BuildMonthlyChartAsync(ownerAdminId, monthKeys, ct);

        var alerts = await billingService.GetAlertsAsync(ownerAdminId, ct);

        return new DashboardKpiResponse(
            TotalCollectedThisMonth: totalCollected,
            TotalPendingDebt: totalPendingDebt,
            TotalResidents: totalResidents,
            OccupiedUnits: occupiedUnits,
            EmptyUnits: emptyUnits,
            // "Morosidad crítica": solo deuda VENCIDA (US029 suma las cuotas en rojo a este KPI).
            TotalDebt: delinquency.OverdueDebt,
            RecentPayments: recentPayments,
            TotalAdminExpenses: totalAdminExpenses,
            AdminExpenses: adminExpenses,
            MonthlyChart: monthlyChart,
            Delinquency: delinquency,
            BillingAlerts: alerts);
    }

    public async Task<MonthlyChartPointDto> GetMonthlyBalanceAsync(
        int ownerAdminId,
        string monthKey,
        CancellationToken ct = default)
    {
        var rows = await BuildMonthlyChartAsync(ownerAdminId, [monthKey], ct);
        return rows[0];
    }

    /// <summary>Partida cobrable (recibo o cuota legacy) con su fecha de pago si se conoce.</summary>
    private sealed record DebtItem(
        string ResidentId,
        decimal Amount,
        string Status,
        string? DueDate,
        bool Paid,
        DateOnly? PaidOn);

    /// <summary>
    /// Fuentes de deuda: receipts (cuotas mensuales + cargos extra/mora) y fees (cuotas legacy del seed).
    /// Ambas se cobran por separado en el checkout de Mercado Pago, así que sumarlas no duplica deuda.
    /// </summary>
    private async Task<List<DebtItem>> LoadDebtItemsAsync(int ownerAdminId, CancellationToken ct)
    {
        var receipts = await dbContext.Set<Receipt>()
            .AsNoTracking()
            .Where(r => r.OwnerAdminId == ownerAdminId)
            .ToListAsync(ct);
        var fees = await dbContext.Set<Fee>()
            .AsNoTracking()
            .Where(f => f.OwnerAdminId == ownerAdminId)
            .ToListAsync(ct);
        var payments = await dbContext.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.OwnerAdminId == ownerAdminId && p.PaidAt != null)
            .Select(p => new { p.ExternalId, p.FeeExternalId, p.PaidAt })
            .ToListAsync(ct);

        // Pagos de checkout: MP-{mpId}-R-{receiptId}; cuotas: fee_external_id.
        var receiptPaidOn = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        var feePaidOn = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var p in payments)
        {
            if (!FinanceClock.TryParseLocalDate(p.PaidAt, out var paidOn)) continue;
            var marker = p.ExternalId.IndexOf("-R-", StringComparison.Ordinal);
            if (marker >= 0)
                receiptPaidOn.TryAdd(p.ExternalId[(marker + 3)..], paidOn);
            if (!string.IsNullOrWhiteSpace(p.FeeExternalId))
                feePaidOn.TryAdd(p.FeeExternalId, paidOn);
        }

        var items = receipts.Select(r => new DebtItem(
            r.ResidentExternalId,
            r.Total,
            r.Status,
            r.DueDate,
            r.IsPaid,
            receiptPaidOn.TryGetValue(r.ExternalId, out var rp) ? rp : null)).ToList();

        items.AddRange(fees.Select(f => new DebtItem(
            f.ResidentExternalId,
            f.Amount,
            f.Status,
            f.DueDate,
            FeeStatus.IsPaid(f.Status),
            feePaidOn.TryGetValue(f.ExternalId, out var fp) ? fp : null)));

        return items;
    }

    private async Task<DelinquencyKpiDto> BuildDelinquencyAsync(
        int ownerAdminId,
        List<DebtItem> items,
        CancellationToken ct)
    {
        var today = FinanceClock.Today();
        var overdueNow = items
            .Where(i => !i.Paid && DelinquencyPolicy.IsOverdue(i.Status, i.DueDate, today))
            .ToList();
        var overdueDebt = overdueNow.Sum(i => i.Amount);

        // Foto al cierre del mes anterior: vencida a esa fecha y todavía impaga entonces
        // (impaga hoy, o pagada después del cierre). Pagos sin fecha conocida se asumen previos.
        var previousClose = Period.FromDate(today).FirstDay.AddDays(-1);
        var previousMonthDebt = items
            .Where(i => MonthlyCharge.TryParseYmd(i.DueDate, out var due) && previousClose > due)
            .Where(i => !i.Paid || (i.PaidOn is { } paidOn && paidOn > previousClose))
            .Sum(i => i.Amount);

        var delta = overdueDebt - previousMonthDebt;
        var trend = delta > 0 ? "up" : delta < 0 ? "down" : "flat";

        var budget = await ExpectedMonthlyBudgetAsync(ownerAdminId, today, ct);
        var threshold = DelinquencyPolicy.DefaultCriticalThreshold;

        return new DelinquencyKpiDto(
            OverdueDebt: overdueDebt,
            PreviousMonthDebt: previousMonthDebt,
            Trend: trend,
            TrendDelta: delta,
            MonthlyBudget: budget,
            Threshold: threshold,
            CriticalAmount: decimal.Round(budget * threshold, 2),
            IsCritical: DelinquencyPolicy.IsCritical(overdueDebt, budget, threshold),
            OverdueResidents: overdueNow.Select(i => i.ResidentId).Distinct().Count());
    }

    /// <summary>
    /// Presupuesto mensual esperado = cuota del mes × residentes que la deben.
    /// Sin cuota del mes se usa la última configurada; sin ninguna, 0 (y la alerta de facturación lo avisa).
    /// </summary>
    private async Task<decimal> ExpectedMonthlyBudgetAsync(int ownerAdminId, DateOnly today, CancellationToken ct)
    {
        var setting = await dbContext.Set<FinanceSetting>()
            .AsNoTracking()
            .Where(s => s.OwnerAdminId == ownerAdminId)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(ct);
        if (setting is null) return 0;

        var period = Period.FromDate(today);
        var charge = setting.FindCharge(period) ?? setting.GetMonthlyCharges().FirstOrDefault();
        if (charge is null || charge.Amount <= 0) return 0;

        var residents = await dbContext.Set<User>()
            .AsNoTracking()
            .Where(u => u.Role == "resident" && u.OwnerAdminId == ownerAdminId)
            .ToListAsync(ct);
        var owing = residents.Count(r => MonthlyBillingService.ResidentOwesMonth(r, period, setting));
        if (owing == 0) owing = residents.Count;

        return charge.Amount * owing;
    }

    /// <summary>Mes contable (Lima) de un paid_at: un instante UTC del día 1 a las 02:00 es aún el mes anterior.</summary>
    private static string LocalMonthKey(string? value) =>
        FinanceClock.TryParseLocalDate(value, out var date)
            ? date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)
            : MonthKeyFromIso(value);

    private static string MonthKeyFromIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 7)
            return string.Empty;

        return value[..7];
    }

    private async Task<IReadOnlyList<MonthlyChartPointDto>> BuildMonthlyChartAsync(
        int ownerAdminId,
        IReadOnlyList<string> monthKeys,
        CancellationToken ct)
    {
        var payments = await dbContext.Set<Payment>()
            .Where(p => p.OwnerAdminId == ownerAdminId && p.PaidAt != null)
            .ToListAsync(ct);

        var paidReceipts = await dbContext.Set<Receipt>()
            .Where(r => r.OwnerAdminId == ownerAdminId && r.Status == FeeStatus.Paid)
            .ToListAsync(ct);

        var expenses = await dbContext.Set<AdminManagementExpense>()
            .Where(e => e.OwnerAdminId == ownerAdminId)
            .ToListAsync(ct);

        return monthKeys
            .Select(monthKey =>
            {
                var incomeFromPayments = payments
                    .Where(p => LocalMonthKey(p.PaidAt) == monthKey)
                    .Sum(p => p.Amount);

                var incomeFromReceipts = paidReceipts
                    .Where(r => MonthKeyFromIso(r.IssueDate) == monthKey)
                    .Sum(r => r.Amount + r.LateFee + r.ExtraCharges);

                var income = incomeFromPayments > 0 ? incomeFromPayments : incomeFromReceipts;

                var expenseTotal = expenses
                    .Where(e => MonthKeyFromIso(e.PurchaseDate) == monthKey)
                    .Sum(e => e.Amount);

                return new MonthlyChartPointDto(monthKey, income, expenseTotal);
            })
            .ToList();
    }
}
