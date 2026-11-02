using BuildingFex.Api.Finances.Application.Internal.Billing;

namespace BuildingFex.Api.Finances.Application.Internal.Dashboard;

public record RecentPaymentDto(
    string Id,
    string ResidentId,
    decimal Amount,
    string? PaidAt,
    string? Method,
    string? Reference);

public record AdminExpenseSummaryDto(
    string Name,
    decimal Amount,
    string PurchaseDate);

public record MonthlyChartPointDto(
    string MonthKey,
    decimal Income,
    decimal Expenses)
{
    /// <summary>Balance neto del mes (RF030): ingresos − egresos.</summary>
    public decimal Net => Income - Expenses;
}

/// <summary>
/// KPI "Morosidad crítica" (RF023 / US023): deuda vencida actual vs. la del cierre del mes anterior,
/// y si supera el umbral (15 %) del presupuesto mensual esperado.
/// </summary>
public record DelinquencyKpiDto(
    decimal OverdueDebt,
    decimal PreviousMonthDebt,
    string Trend,
    decimal TrendDelta,
    decimal MonthlyBudget,
    decimal Threshold,
    decimal CriticalAmount,
    bool IsCritical,
    int OverdueResidents);

public record DashboardKpiResponse(
    decimal TotalCollectedThisMonth,
    decimal TotalPendingDebt,
    int TotalResidents,
    int OccupiedUnits,
    int EmptyUnits,
    decimal TotalDebt,
    IEnumerable<RecentPaymentDto> RecentPayments,
    decimal TotalAdminExpenses,
    IEnumerable<AdminExpenseSummaryDto> AdminExpenses,
    IReadOnlyList<MonthlyChartPointDto> MonthlyChart,
    DelinquencyKpiDto Delinquency,
    IReadOnlyList<BillingAlert> BillingAlerts);
