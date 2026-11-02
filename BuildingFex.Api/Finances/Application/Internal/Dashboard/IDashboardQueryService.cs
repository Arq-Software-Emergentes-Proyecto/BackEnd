namespace BuildingFex.Api.Finances.Application.Internal.Dashboard;

public interface IDashboardQueryService
{
    Task<DashboardKpiResponse> GetKpisAsync(int ownerAdminId, CancellationToken ct = default);

    /// <summary>Ingresos, egresos y balance neto de un mes "yyyy-MM" (RF030).</summary>
    Task<MonthlyChartPointDto> GetMonthlyBalanceAsync(int ownerAdminId, string monthKey, CancellationToken ct = default);
}
