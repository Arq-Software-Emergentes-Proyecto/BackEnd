namespace BuildingFex.Api.Billing.Interfaces.Rest.Resources;

/// <summary>
/// GET /api/v1/subscription. Los 6 primeros campos son los de la respuesta anterior (compatibilidad);
/// el resto expone el agregado Subscription (estado, prueba Free) y los topes del plan.
/// </summary>
public record SubscriptionResource(
    string PlanId,
    int ResidentLimit,
    int ResidentsCount,
    decimal MonthlyPricePen,
    string? PaidUntil,
    bool IsPaid,
    string Status,
    string? TrialEndsAt,
    int? TrialDaysLeft,
    bool TrialExpired,
    bool RequiresPlanSelection,
    bool CanAddResidents,
    int MaxDepartments,
    int MonthlyAiQueries,
    int BillableDepartments,
    int? BuildingId,
    string SuggestedPlanId);

/// <summary>GET /api/v1/subscription/limits (PlanQuotaQuery del informe, 5.4.2).</summary>
public record SubscriptionLimitsResource(
    string PlanId,
    string Status,
    int MaxDepartments,
    int DepartmentsUsed,
    int DepartmentsAvailable,
    int MonthlyAiQueries,
    int? AiQueriesUsed,
    bool CanAddResidents);

public record PlanResource(
    string Id,
    string Name,
    decimal MonthlyPricePen,
    decimal? PricePerDepartmentPen,
    decimal? MinimumMonthlyPricePen,
    int MinDepartments,
    int MaxDepartments,
    int MonthlyAiQueries,
    int? TrialDays,
    bool IsTrial,
    bool IsPaid,
    string Support,
    IReadOnlyList<string> Modules,
    bool Highlighted);

/// <summary>GET /api/v1/plans (público).</summary>
public record PlanCatalogResource(
    string Currency,
    int TrialDays,
    IReadOnlyList<string> Modules,
    IReadOnlyList<PlanResource> Plans);
