using BuildingFex.Api.Finances.Application.Internal.Dashboard;
using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Repositories;
using BuildingFex.Api.Finances.Interfaces.Rest.Transform;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BuildingFex.Api.Finances.Interfaces.Rest.Compat;

[ApiController]
[Route("kpi")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class KpiCompatController(
    IKpiRepository kpiRepository,
    IDashboardQueryService dashboardQueryService,
    IUnitOfWork unitOfWork) : TenantScopedControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? ownerAdminId, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        var kpis = await dashboardQueryService.GetKpisAsync(owner.Id, ct);

        return Ok(new[]
        {
            new
            {
                id = $"kpi-{owner.ExternalId}",
                ownerAdminId = owner.ExternalId,
                totalResidents = kpis.TotalResidents,
                occupiedUnits = kpis.OccupiedUnits,
                emptyUnits = kpis.EmptyUnits,
                totalDebt = kpis.TotalDebt,
                totalCollectedThisMonth = kpis.TotalCollectedThisMonth,
                totalPendingDebt = kpis.TotalPendingDebt,
                monthlyChart = kpis.MonthlyChart.Select(m => new
                {
                    monthKey = m.MonthKey,
                    income = m.Income,
                    expenses = m.Expenses,
                    net = m.Net,
                }),
                delinquency = DashboardKpiJson.Delinquency(kpis.Delinquency),
                billingAlerts = DashboardKpiJson.Alerts(kpis.BillingAlerts),
            },
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(BodyString(body, "ownerAdminId"), ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        var externalId = body.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? $"kpi-{owner.ExternalId}"
            : $"kpi-{owner.ExternalId}";

        var kpi = KpiRecord.Create(
            externalId,
            owner.Id,
            body.TryGetProperty("totalResidents", out var totalProp) ? totalProp.GetInt32() : 0,
            body.TryGetProperty("occupiedUnits", out var occupiedProp) ? occupiedProp.GetInt32() : 0,
            body.TryGetProperty("emptyUnits", out var emptyProp) ? emptyProp.GetInt32() : 0,
            body.TryGetProperty("totalDebt", out var debtProp) ? debtProp.GetDecimal() : 0);

        await kpiRepository.AddAsync(kpi, ct);
        await unitOfWork.CompleteAsync(ct);

        var created = await kpiRepository.FindByExternalIdAsync(externalId, ct);
        return StatusCode(StatusCodes.Status201Created, FinanceCompatSerializer.KpiToJson(created!));
    }
}
