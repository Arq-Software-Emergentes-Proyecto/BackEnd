namespace BuildingFex.Api.Billing.Domain.Model;

public static class SubscriptionPlanIds
{
    public const string Free = "free";
    public const string Essential = "essential";
    public const string Standard = "standard";
    public const string Scale = "scale";
    public const string Portafolio = "portafolio";
}

/// <summary>
/// Catálogo de planes (informe TP1, 1.2 Solution Profile): Free = prueba de 30 días con
/// funciones de Essential; Essential S/79 (hasta 15 dptos.), Standard S/159 (16–40),
/// Scale S/289 (41–80) y Portafolio S/4 por dpto. con mínimo S/600 (desde 150 dptos.).
/// Topes mensuales del asistente de IA: Free 150 (prueba), Essential 750, Standard 2,000, Scale 4,000 y
/// Portafolio 10,000 consultas (unas 50 por departamento en el tamaño máximo de cada plan).
/// </summary>
public static class SubscriptionPlans
{
    public const int FreeTrialDays = 30;

    /// <summary>
    /// Días de gracia tras <c>paid_until</c> de un plan de pago (no hay cobro recurrente automático): durante la
    /// gracia el estado efectivo es PAYMENT_FAILED pero se conservan los topes del plan; después queda SUSPENDED —
    /// no se amplía el padrón y la IA usa el tope de Free/Essential. Las lecturas nunca se bloquean.
    /// </summary>
    public const int PaidGraceDays = 5;

    /// <summary>Días que agrega cada cobro aprobado (desde el vencimiento vigente, o desde hoy si ya venció).</summary>
    public const int PaidPeriodDays = 31;
    public const decimal PortafolioPricePerDepartmentPen = 4m;
    public const decimal PortafolioMinimumPen = 600m;
    public const int PortafolioMinimumDepartments = 150;

    private static readonly Dictionary<string, int> ResidentLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        [SubscriptionPlanIds.Free] = 15,
        [SubscriptionPlanIds.Essential] = 15,
        [SubscriptionPlanIds.Standard] = 40,
        [SubscriptionPlanIds.Scale] = 80,
        [SubscriptionPlanIds.Portafolio] = 5000,
    };

    private static readonly Dictionary<string, decimal> MonthlyPricesPen = new(StringComparer.OrdinalIgnoreCase)
    {
        [SubscriptionPlanIds.Free] = 0m,
        [SubscriptionPlanIds.Essential] = 79m,
        [SubscriptionPlanIds.Standard] = 159m,
        [SubscriptionPlanIds.Scale] = 289m,
        [SubscriptionPlanIds.Portafolio] = PortafolioMinimumPen,
    };

    private static readonly Dictionary<string, int> MonthlyAiQueryCaps = new(StringComparer.OrdinalIgnoreCase)
    {
        [SubscriptionPlanIds.Free] = 150,
        [SubscriptionPlanIds.Essential] = 750,
        [SubscriptionPlanIds.Standard] = 2000,
        [SubscriptionPlanIds.Scale] = 4000,
        [SubscriptionPlanIds.Portafolio] = 10000,
    };

    public static IReadOnlyList<string> All { get; } =
    [
        SubscriptionPlanIds.Free,
        SubscriptionPlanIds.Essential,
        SubscriptionPlanIds.Standard,
        SubscriptionPlanIds.Scale,
        SubscriptionPlanIds.Portafolio,
    ];

    public static bool IsValid(string? planId) =>
        !string.IsNullOrWhiteSpace(planId) &&
        ResidentLimits.ContainsKey(planId.Trim());

    public static string Normalize(string? planId) =>
        IsValid(planId) ? planId!.Trim().ToLowerInvariant() : SubscriptionPlanIds.Free;

    public static int MaxResidents(string? planId) =>
        ResidentLimits.TryGetValue(Normalize(planId), out var limit)
            ? limit
            : ResidentLimits[SubscriptionPlanIds.Free];

    /// <summary>Precio mensual base (para Portafolio, el mínimo S/600).</summary>
    public static decimal MonthlyPricePen(string? planId) =>
        MonthlyPricesPen.TryGetValue(Normalize(planId), out var price)
            ? price
            : 0m;

    /// <summary>Precio mensual según cantidad de departamentos (Portafolio cobra S/4 por dpto., mínimo S/600).</summary>
    public static decimal MonthlyPricePen(string? planId, int departmentCount)
    {
        var normalized = Normalize(planId);
        if (normalized != SubscriptionPlanIds.Portafolio) return MonthlyPricePen(normalized);
        return Math.Max(PortafolioMinimumPen, PortafolioPricePerDepartmentPen * Math.Max(0, departmentCount));
    }

    /// <summary>Tope mensual de consultas al asistente de IA por edificio.</summary>
    public static int MonthlyAiQueryCap(string? planId) =>
        MonthlyAiQueryCaps.TryGetValue(Normalize(planId), out var cap)
            ? cap
            : MonthlyAiQueryCaps[SubscriptionPlanIds.Free];

    public static bool IsPaid(string? planId) => MonthlyPricePen(planId) > 0m;

    /// <summary>¿El periodo pagado venció hace más de <see cref="PaidGraceDays"/> días? (null = sin vencimiento conocido).</summary>
    public static bool IsPaidPeriodLapsed(string? planId, DateTimeOffset? paidUntil, DateTimeOffset now) =>
        IsPaid(planId) && paidUntil.HasValue && paidUntil.Value.AddDays(PaidGraceDays) <= now;

    /// <summary>
    /// Plan efectivo para topes (departamentos, IA): un plan de pago vencido (pasada la gracia) se trata como Free.
    /// Se calcula igual desde el agregado Subscription o desde el espejo users.subscription_plan_id/paid_until.
    /// </summary>
    public static string EffectivePlanId(string? planId, DateTimeOffset? paidUntil, DateTimeOffset now) =>
        IsPaidPeriodLapsed(planId, paidUntil, now) ? SubscriptionPlanIds.Free : Normalize(planId);
}
