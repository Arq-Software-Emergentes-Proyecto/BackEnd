namespace BuildingFex.Api.Billing.Domain.Model.ValueObjects;

/// <summary>Topes del plan (informe, 5.4.1 — PlanLimits): departamentos y consultas de IA al mes.</summary>
public sealed record PlanLimits(int MaxDepartments, int MonthlyAiQueries)
{
    public static PlanLimits For(string? planId) =>
        new(SubscriptionPlans.MaxResidents(planId), SubscriptionPlans.MonthlyAiQueryCap(planId));

    public bool AllowsDepartments(int departmentCount) => departmentCount <= MaxDepartments;

    public bool AllowsAiQuery(int usedThisMonth) => usedThisMonth < MonthlyAiQueries;
}
