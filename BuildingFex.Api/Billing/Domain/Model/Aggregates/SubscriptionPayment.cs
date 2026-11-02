using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Billing.Domain.Model.Aggregates;

public static class BillingIndexes
{
    public const string SubscriptionPaymentMpId = "ux_subscription_payments_mp_payment_id";
}

/// <summary>Estado de un cobro de suscripción registrado.</summary>
public static class SubscriptionPaymentStatus
{
    /// <summary>Cobro aplicado: extendió el periodo pagado.</summary>
    public const string Applied = "APPLIED";

    /// <summary>Monto/moneda no coinciden con el snapshot del checkout: no se activó, queda para revisión manual.</summary>
    public const string ReviewAmountMismatch = "REVIEW_AMOUNT_MISMATCH";
}

/// <summary>
/// Cobro de Mercado Pago de una suscripción (tabla <c>subscription_payments</c>). <c>mp_payment_id</c> es único:
/// el mismo pago (confirm + webhook, reintentos de MP, replays) se aplica una sola vez.
/// </summary>
public class SubscriptionPayment : IAuditableEntity
{
    public int Id { get; private set; }
    public string MpPaymentId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public string PlanId { get; private set; } = SubscriptionPlanIds.Free;
    public decimal Amount { get; private set; }
    public decimal ExpectedAmount { get; private set; }
    public string? Currency { get; private set; }
    public int? DepartmentCount { get; private set; }
    public string? CheckoutExternalId { get; private set; }
    public string Status { get; private set; } = SubscriptionPaymentStatus.Applied;

    /// <summary>"confirm" | "webhook" | "demo".</summary>
    public string Source { get; private set; } = "webhook";

    /// <summary>Nuevo vencimiento resultante (solo APPLIED).</summary>
    public DateTimeOffset? PaidUntil { get; private set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private SubscriptionPayment()
    {
    }

    public static SubscriptionPayment Create(
        string mpPaymentId,
        int ownerAdminId,
        string planId,
        decimal amount,
        decimal expectedAmount,
        string? currency,
        int? departmentCount,
        string? checkoutExternalId,
        string status,
        string source,
        DateTimeOffset? paidUntil)
    {
        var now = DateTimeOffset.UtcNow;
        return new SubscriptionPayment
        {
            MpPaymentId = mpPaymentId.Trim(),
            OwnerAdminId = ownerAdminId,
            PlanId = SubscriptionPlans.Normalize(planId),
            Amount = amount,
            ExpectedAmount = expectedAmount,
            Currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant(),
            DepartmentCount = departmentCount,
            CheckoutExternalId = string.IsNullOrWhiteSpace(checkoutExternalId) ? null : checkoutExternalId.Trim(),
            Status = status,
            Source = source,
            PaidUntil = paidUntil,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsApplied => Status == SubscriptionPaymentStatus.Applied;
}

/// <summary>
/// Snapshot del checkout de suscripción (tabla <c>subscription_checkouts</c>): plan, departamentos y monto fijados
/// al crear la preferencia. El pago se verifica contra este monto, así un cambio de precios no rechaza pagos en curso.
/// </summary>
public class SubscriptionCheckout : IAuditableEntity
{
    public const string StatusPending = "PENDING";
    public const string StatusPaid = "PAID";

    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public string PlanId { get; private set; } = SubscriptionPlanIds.Free;
    public int? DepartmentCount { get; private set; }
    public decimal Amount { get; private set; }
    public string Status { get; private set; } = StatusPending;
    public string? MpPaymentId { get; private set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private SubscriptionCheckout()
    {
    }

    public static SubscriptionCheckout Create(int ownerAdminId, string planId, int? departmentCount, decimal amount)
    {
        var now = DateTimeOffset.UtcNow;
        return new SubscriptionCheckout
        {
            // "subchk-" + 32 hex: sin ':' (separador de la external_reference de MP).
            ExternalId = $"subchk-{Guid.NewGuid():N}",
            OwnerAdminId = ownerAdminId,
            PlanId = SubscriptionPlans.Normalize(planId),
            DepartmentCount = departmentCount,
            Amount = amount,
            Status = StatusPending,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void MarkPaid(string mpPaymentId)
    {
        Status = StatusPaid;
        MpPaymentId = mpPaymentId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
