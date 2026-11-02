using System.Text.Json;
using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Finances.Domain.Model.Aggregates;

/// <summary>
/// Snapshot de un checkout de mantenimiento (RNF002/RNF008, QAS03).
/// Al crear la preferencia de Mercado Pago se fijan las cuotas/recibos y el total a pagar;
/// la conciliación (webhook o confirm) solo paga ESOS ítems, exige que el monto aprobado coincida
/// y guarda el id de pago de MP (único) como marcador de idempotencia.
/// </summary>
public class MaintenanceCheckout : IAuditableEntity
{
    public const string StatusPending = "pending";
    public const string StatusPaid = "paid";
    /// <summary>Pagado en MP pero algún ítem ya estaba pagado (p.ej. dos checkouts del mismo mes) → revisión manual.</summary>
    public const string StatusPaidReview = "paid_review";

    public const string ItemReceipt = "receipt";
    public const string ItemFee = "fee";

    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public string ResidentExternalId { get; private set; } = string.Empty;
    public string ItemsJson { get; private set; } = "[]";
    public decimal TotalAmount { get; private set; }
    public string Status { get; private set; } = StatusPending;
    public string? MpPaymentId { get; private set; }
    public string? PreferenceId { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private MaintenanceCheckout()
    {
    }

    public sealed record Item(string Type, string ExternalId, decimal Amount);

    public static MaintenanceCheckout Create(
        int ownerAdminId,
        string residentExternalId,
        IReadOnlyCollection<Item> items)
    {
        if (items.Count == 0)
            throw new InvalidOperationException("Checkout sin ítems.");

        return new MaintenanceCheckout
        {
            ExternalId = $"chk-{Guid.NewGuid():N}",
            OwnerAdminId = ownerAdminId,
            ResidentExternalId = residentExternalId.Trim(),
            ItemsJson = JsonSerializer.Serialize(items),
            TotalAmount = items.Sum(i => i.Amount),
            Status = StatusPending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public IReadOnlyList<Item> GetItems()
    {
        try
        {
            return JsonSerializer.Deserialize<List<Item>>(ItemsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public bool IsPending => Status == StatusPending;

    public void AttachPreference(string? preferenceId)
    {
        PreferenceId = string.IsNullOrWhiteSpace(preferenceId) ? null : preferenceId.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkPaid(string mpPaymentId, DateTimeOffset paidAt, bool needsReview)
    {
        if (!IsPending)
            throw new InvalidOperationException("El checkout ya fue conciliado.");

        MpPaymentId = mpPaymentId;
        PaidAt = paidAt;
        Status = needsReview ? StatusPaidReview : StatusPaid;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool AmountMatches(decimal paidAmount) => Math.Abs(paidAmount - TotalAmount) <= 0.01m;
}
