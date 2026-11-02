using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Billing.Interfaces.Rest.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Billing.Interfaces.Rest;

/// <summary>
/// Catálogo público de planes (RF004 / RF005 — US004 / US005). Anónimo: lo usan la landing,
/// el registro y Ajustes → Planes. Precios en soles (PEN), mensuales.
/// </summary>
[ApiController]
[Route("api/v1/plans")]
[AllowAnonymous]
public class PlansController : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 300)]
    public IActionResult GetAll() =>
        Ok(new PlanCatalogResource(
            "PEN",
            SubscriptionPlans.FreeTrialDays,
            PlanModules.All,
            PlanCatalog.All.Select(ToResource).ToList()));

    [HttpGet("{planId}")]
    public IActionResult GetById(string planId)
    {
        if (!SubscriptionPlans.IsValid(planId))
            return NotFound(new { code = "PLAN_NOT_FOUND", message = "Plan no encontrado." });
        var plan = PlanCatalog.Find(planId);
        return plan is null
            ? NotFound(new { code = "PLAN_NOT_FOUND", message = "Plan no encontrado." })
            : Ok(ToResource(plan));
    }

    /// <summary>Plan sugerido según la cantidad de departamentos (US015).</summary>
    [HttpGet("suggest")]
    public IActionResult Suggest([FromQuery] int departments) =>
        Ok(new { departments, planId = PlanCatalog.SuggestForDepartments(departments) });

    internal static PlanResource ToResource(PlanDefinition plan) =>
        new(
            plan.Id,
            plan.Name,
            plan.MonthlyPricePen,
            plan.PricePerDepartmentPen,
            plan.MinimumMonthlyPricePen,
            plan.MinDepartments,
            plan.MaxDepartments,
            plan.MonthlyAiQueries,
            plan.TrialDays,
            plan.TrialDays.HasValue,
            SubscriptionPlans.IsPaid(plan.Id),
            plan.Support,
            plan.Modules,
            plan.Highlighted);
}
