using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Finances.Domain.Model.Aggregates;

/// <summary>Tipo de recibo: la cuota mensual (una por residente y mes) o un cargo extra suelto.</summary>
public static class ReceiptKind
{
    /// <summary>Cuota mensual de mantenimiento: única por (owner, residente, billing_month) — RNF011.</summary>
    public const string Monthly = "MONTHLY";

    /// <summary>Cargo extra / servicio compartido emitido aparte: no cuenta como "mes facturado".</summary>
    public const string Extra = "EXTRA";

    /// <summary>
    /// "extra"/"monthly" explícito (case-insensitive); si no viene, se infiere EXTRA cuando el recibo no trae
    /// cuota (amount = 0) y sí cargos extra — así lo emiten el FE de cargos extra y el de servicios compartidos.
    /// </summary>
    public static string Resolve(string? requested, decimal amount, decimal extraCharges)
    {
        var raw = (requested ?? string.Empty).Trim();
        if (raw.Equals(Extra, StringComparison.OrdinalIgnoreCase)) return Extra;
        if (raw.Equals(Monthly, StringComparison.OrdinalIgnoreCase)) return Monthly;
        return amount == 0 && extraCharges > 0 ? Extra : Monthly;
    }
}

/// <summary>Nombres de índices de receipts (EF + SchemaUpgrade + detección de duplicados).</summary>
public static class ReceiptIndexes
{
    public const string UniqueMonthly = "ux_receipts_owner_resident_monthly_key";
    public const string ByBillingMonth = "ix_receipts_owner_resident_billing_month";

    /// <summary>Índice único anterior (owner, residente, billing_month): se elimina en SchemaUpgrade.</summary>
    public const string LegacyUniqueBillingMonth = "ux_receipts_owner_resident_billing_month";
}

public class Receipt : IAuditableEntity, IOwnerScopedFinanceEntity
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public User? OwnerAdmin { get; private set; }
    public string ResidentExternalId { get; private set; } = string.Empty;
    public string? IssueDate { get; private set; }
    public string? DueDate { get; private set; }
    public decimal Amount { get; private set; }
    public string Status { get; private set; } = "Pending";
    public decimal LateFee { get; private set; }
    public decimal ExtraCharges { get; private set; }
    public string? Concept { get; private set; }

    /// <summary>Mes facturado "yyyy-MM" (RF021). Puede repetirse entre la cuota y los cargos extra del mismo mes.</summary>
    public string? BillingMonth { get; private set; }

    /// <summary><see cref="ReceiptKind.Monthly"/> o <see cref="ReceiptKind.Extra"/>.</summary>
    public string Kind { get; private set; } = ReceiptKind.Monthly;

    /// <summary>
    /// = <see cref="BillingMonth"/> solo para la cuota mensual; null para cargos extra. Índice único
    /// (owner_admin_id, resident_external_id, monthly_key): un único recibo MENSUAL por residente y mes (RNF011);
    /// MySQL admite varios NULL, así los cargos extra del mes no chocan con la cuota.
    /// </summary>
    public string? MonthlyKey { get; private set; }

    /// <summary>Fecha "yyyy-MM-dd" desde la que el residente puede pagar (1.º del mes siguiente por defecto).</summary>
    public string? PaymentOpen { get; private set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private Receipt() { }

    public static Receipt Create(
        string externalId,
        int ownerAdminId,
        string residentExternalId,
        string? issueDate,
        string? dueDate,
        decimal amount,
        string status,
        decimal lateFee,
        decimal extraCharges,
        string? concept,
        string? billingMonth = null,
        string? paymentOpen = null,
        string? kind = null)
    {
        var resolvedKind = ReceiptKind.Resolve(kind, amount, extraCharges);
        var month = Period.Normalize(billingMonth);
        return new Receipt
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId,
            ResidentExternalId = residentExternalId,
            IssueDate = issueDate,
            DueDate = dueDate,
            Amount = amount,
            Status = status,
            LateFee = lateFee,
            ExtraCharges = extraCharges,
            Concept = concept,
            BillingMonth = month,
            Kind = resolvedKind,
            MonthlyKey = resolvedKind == ReceiptKind.Monthly ? month : null,
            PaymentOpen = string.IsNullOrWhiteSpace(paymentOpen) ? null : paymentOpen.Trim(),
        };
    }

    /// <summary>Recibo de la cuota mensual generado por la facturación automática (US021 / TS003).</summary>
    public static Receipt CreateMonthly(
        string externalId,
        int ownerAdminId,
        string residentExternalId,
        MonthlyCharge charge,
        DateOnly issueDate) =>
        Create(
            externalId,
            ownerAdminId,
            residentExternalId,
            MonthlyCharge.Ymd(issueDate),
            charge.PaymentDue,
            charge.Amount,
            FeeStatus.Pending,
            0,
            0,
            "Mantenimiento",
            charge.Month,
            charge.PaymentOpen,
            ReceiptKind.Monthly);

    public bool IsPaid => FeeStatus.IsPaid(Status);

    public bool IsMonthly => !string.Equals(Kind, ReceiptKind.Extra, StringComparison.OrdinalIgnoreCase);

    public decimal Total => Amount + LateFee + ExtraCharges;

    /// <summary>
    /// ¿Este recibo cubre el mes indicado? Exacto por billing_month; los recibos legacy sin mes
    /// se asocian por vencimiento: la cuota del mes M vence en M+1 (computeChargeDates del frontend).
    /// Un cargo extra (<see cref="ReceiptKind.Extra"/>) nunca cubre la cuota del mes.
    /// </summary>
    public bool CoversMonth(Period month)
    {
        if (!IsMonthly) return false;
        if (!string.IsNullOrWhiteSpace(BillingMonth))
            return string.Equals(BillingMonth, month.Key, StringComparison.Ordinal);
        return (DueDate ?? string.Empty).StartsWith(month.Next().Key, StringComparison.Ordinal);
    }

    /// <summary>Mes contable efectivo: billing_month o, para legacy, el mes anterior al vencimiento.</summary>
    public string? EffectiveBillingMonth()
    {
        if (!string.IsNullOrWhiteSpace(BillingMonth)) return BillingMonth;
        return MonthlyCharge.TryParseYmd(DueDate, out var due) ? Period.FromDate(due).Previous().Key : null;
    }

    public void Patch(string? status, decimal? lateFee, decimal? extraCharges, string? concept)
    {
        if (!string.IsNullOrWhiteSpace(status))
            Status = status;
        if (lateFee.HasValue)
            LateFee = lateFee.Value;
        if (extraCharges.HasValue)
            ExtraCharges = extraCharges.Value;
        if (concept is not null)
            Concept = concept;
    }

    public void MarkAsPaid()
    {
        Status = FeeStatus.Paid;
    }
}
