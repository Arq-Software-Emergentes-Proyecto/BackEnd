using BuildingFex.Api.Billing.Domain.Model;

namespace BuildingFex.Api.ResidentCare.Infrastructure.PlanQuota;

/// <summary>
/// ACL hacia Billing: el tope mensual de consultas de IA del plan del edificio.
/// Resident Care no conoce la tabla de planes; solo consume el número que publica Billing.
/// </summary>
public interface IPlanQuotaClient
{
    int MonthlyAiQueryCap(string? subscriptionPlanId);

    /// <summary>Tope del plan efectivo: si el plan de pago venció (paid_until + gracia), rige el plan gratuito.</summary>
    int MonthlyAiQueryCap(string? subscriptionPlanId, DateTimeOffset? paidUntil);
}

public class PlanQuotaClient : IPlanQuotaClient
{
    public int MonthlyAiQueryCap(string? subscriptionPlanId) =>
        SubscriptionPlans.MonthlyAiQueryCap(subscriptionPlanId);

    public int MonthlyAiQueryCap(string? subscriptionPlanId, DateTimeOffset? paidUntil) =>
        SubscriptionPlans.MonthlyAiQueryCap(
            SubscriptionPlans.EffectivePlanId(subscriptionPlanId, paidUntil, DateTimeOffset.UtcNow));
}
