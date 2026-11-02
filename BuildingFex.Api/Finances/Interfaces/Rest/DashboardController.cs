using BuildingFex.Api.Finances.Application.Internal.Dashboard;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Finances.Interfaces.Rest;

[ApiController]
[Route("api/v1/finances")]
[Tags("Dashboard")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class DashboardController(
    IDashboardQueryService dashboardQueryService) : TenantScopedControllerBase
{
    [HttpGet("kpi")]
    public async Task<IActionResult> GetKpis(
        [FromQuery] string? ownerAdminId,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        var kpis = await dashboardQueryService.GetKpisAsync(owner.Id, ct);

        return Ok(new
        {
            totalCollectedThisMonth = kpis.TotalCollectedThisMonth,
            totalPendingDebt = kpis.TotalPendingDebt,
            totalResidents = kpis.TotalResidents,
            occupiedUnits = kpis.OccupiedUnits,
            emptyUnits = kpis.EmptyUnits,
            totalDebt = kpis.TotalDebt,
            recentPayments = kpis.RecentPayments.Select(p => new
            {
                id = p.Id,
                residentId = p.ResidentId,
                amount = p.Amount,
                paidAt = p.PaidAt,
                method = p.Method,
                reference = p.Reference,
            }),
            totalAdminExpenses = kpis.TotalAdminExpenses,
            adminExpenses = kpis.AdminExpenses.Select(e => new
            {
                name = e.Name,
                amount = e.Amount,
                purchaseDate = e.PurchaseDate,
            }),
            monthlyChart = kpis.MonthlyChart.Select(m => new
            {
                monthKey = m.MonthKey,
                income = m.Income,
                expenses = m.Expenses,
                net = m.Net,
            }),
            delinquency = DashboardKpiJson.Delinquency(kpis.Delinquency),
            billingAlerts = DashboardKpiJson.Alerts(kpis.BillingAlerts),
        });
    }
}

/// <summary>Forma JSON compartida del KPI de morosidad (DashboardController y /kpi compat).</summary>
internal static class DashboardKpiJson
{
    public static object Delinquency(DelinquencyKpiDto d) => new
    {
        totalDebt = d.OverdueDebt,
        previousMonthDebt = d.PreviousMonthDebt,
        trend = d.Trend,
        trendDelta = d.TrendDelta,
        monthlyBudget = d.MonthlyBudget,
        threshold = d.Threshold,
        criticalAmount = d.CriticalAmount,
        isCritical = d.IsCritical,
        overdueResidents = d.OverdueResidents,
    };

    public static object Alerts(IEnumerable<BuildingFex.Api.Finances.Application.Internal.Billing.BillingAlert> alerts) =>
        alerts.Select(a => new { code = a.Code, month = a.Month, message = a.Message }).ToList();
}
