namespace BuildingFex.Api.Billing.Domain.Model;

/// <summary>
/// Ficha comercial de un plan para la vista de planes y la tabla comparativa (US004 / US005).
/// Los textos se traducen en el frontend: aquí solo van claves estables (módulos, soporte).
/// </summary>
public sealed record PlanDefinition(
    string Id,
    string Name,
    decimal MonthlyPricePen,
    decimal? PricePerDepartmentPen,
    decimal? MinimumMonthlyPricePen,
    int MinDepartments,
    int MaxDepartments,
    int MonthlyAiQueries,
    int? TrialDays,
    string Support,
    IReadOnlyList<string> Modules,
    bool Highlighted);

/// <summary>Claves de módulos incluidos por plan (informe TP1, 1.2 — Monetización).</summary>
public static class PlanModules
{
    public const string WebConsole = "webConsole";
    public const string CommonAreas = "commonAreas";
    public const string Collections = "collections";
    public const string Announcements = "announcements";
    public const string Documents = "documents";
    public const string Visits = "visits";
    public const string AiAssistant = "aiAssistant";
    public const string DebtVisibility = "debtVisibility";
    public const string AiIncidentClassification = "aiIncidentClassification";
    public const string ImplementationAssistance = "implementationAssistance";
    public const string MultiBuildingConsole = "multiBuildingConsole";

    /// <summary>Orden de filas de la tabla comparativa (solo lo que la plataforma hace hoy).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        WebConsole, CommonAreas, Collections, Announcements, Documents, Visits, AiAssistant, DebtVisibility,
        AiIncidentClassification,
        ImplementationAssistance,
        MultiBuildingConsole,
    ];
}

/// <summary>Nivel de soporte por plan.</summary>
public static class PlanSupportLevels
{
    public const string EmailBusinessDays = "emailBusinessDays";
    public const string Priority = "priority";
    public const string AgreedResponseTimes = "agreedResponseTimes";
    public const string Sla = "sla";
}

public static class PlanCatalog
{
    private static readonly string[] EssentialModules =
    [
        PlanModules.WebConsole, PlanModules.CommonAreas, PlanModules.Collections,
        PlanModules.Announcements, PlanModules.Documents, PlanModules.Visits, PlanModules.AiAssistant,
        PlanModules.DebtVisibility,
    ];

    private static readonly string[] StandardModules =
    [
        .. EssentialModules,
        PlanModules.AiIncidentClassification,
    ];

    private static readonly string[] ScaleModules =
    [
        .. StandardModules,
        PlanModules.ImplementationAssistance,
    ];

    private static readonly string[] PortafolioModules =
    [
        .. ScaleModules,
        PlanModules.MultiBuildingConsole,
    ];

    public static IReadOnlyList<PlanDefinition> All { get; } =
    [
        Build(SubscriptionPlanIds.Free, "Free", 1, null, PlanSupportLevels.EmailBusinessDays, EssentialModules, trialDays: SubscriptionPlans.FreeTrialDays),
        Build(SubscriptionPlanIds.Essential, "Essential", 1, null, PlanSupportLevels.EmailBusinessDays, EssentialModules),
        Build(SubscriptionPlanIds.Standard, "Standard", 16, null, PlanSupportLevels.Priority, StandardModules, highlighted: true),
        Build(SubscriptionPlanIds.Scale, "Scale", 41, null, PlanSupportLevels.AgreedResponseTimes, ScaleModules),
        Build(SubscriptionPlanIds.Portafolio, "Portafolio", SubscriptionPlans.PortafolioMinimumDepartments,
            SubscriptionPlans.PortafolioPricePerDepartmentPen, PlanSupportLevels.Sla, PortafolioModules),
    ];

    public static PlanDefinition? Find(string? planId) =>
        All.FirstOrDefault(p => string.Equals(p.Id, SubscriptionPlans.Normalize(planId), StringComparison.Ordinal));

    /// <summary>
    /// Plan de pago sugerido según la cantidad de departamentos (US015: "lo dirige a la configuración del plan
    /// según la cantidad de departamentos"). Más de 80 departamentos → Portafolio.
    /// </summary>
    public static string SuggestForDepartments(int departmentCount)
    {
        var count = Math.Max(1, departmentCount);
        foreach (var plan in All)
        {
            if (!SubscriptionPlans.IsPaid(plan.Id)) continue;
            if (plan.Id == SubscriptionPlanIds.Portafolio) continue;
            if (count <= plan.MaxDepartments) return plan.Id;
        }
        return SubscriptionPlanIds.Portafolio;
    }

    private static PlanDefinition Build(
        string id,
        string name,
        int minDepartments,
        decimal? pricePerDepartment,
        string support,
        IReadOnlyList<string> modules,
        int? trialDays = null,
        bool highlighted = false) =>
        new(
            id,
            name,
            SubscriptionPlans.MonthlyPricePen(id),
            pricePerDepartment,
            id == SubscriptionPlanIds.Portafolio ? SubscriptionPlans.PortafolioMinimumPen : null,
            minDepartments,
            SubscriptionPlans.MaxResidents(id),
            SubscriptionPlans.MonthlyAiQueryCap(id),
            trialDays,
            support,
            modules,
            highlighted);
}
