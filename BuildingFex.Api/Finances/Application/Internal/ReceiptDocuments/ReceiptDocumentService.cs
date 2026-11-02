using System.Globalization;
using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Finances.Application.Internal.ReceiptDocuments;

public sealed record ReceiptDocument(string FileName, byte[] Content);

public interface IReceiptDocumentService
{
    /// <summary>Comprobante de un pago registrado (Mercado Pago o manual).</summary>
    Task<ReceiptDocument> ForPaymentAsync(Payment payment, CancellationToken ct = default);

    /// <summary>Comprobante de un recibo pagado. Lanza InvalidOperationException si no está pagado.</summary>
    Task<ReceiptDocument> ForReceiptAsync(Receipt receipt, CancellationToken ct = default);
}

/// <summary>
/// Emite el recibo descargable de un pago aprobado (RF022 / US022, Tabla 60 Receipt.issue()).
/// Solo lee datos ya conciliados: nunca cambia el estado de la deuda.
/// </summary>
public class ReceiptDocumentService(AppDbContext db, IReceiptPdfRenderer renderer) : IReceiptDocumentService
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-PE");

    public async Task<ReceiptDocument> ForPaymentAsync(Payment payment, CancellationToken ct = default)
    {
        Receipt? receipt = null;
        var marker = payment.ExternalId.IndexOf("-R-", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var receiptId = payment.ExternalId[(marker + 3)..];
            receipt = await db.Set<Receipt>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.ExternalId == receiptId && r.OwnerAdminId == payment.OwnerAdminId, ct);
        }

        Fee? fee = null;
        if (receipt is null && !string.IsNullOrWhiteSpace(payment.FeeExternalId))
            fee = await db.Set<Fee>().AsNoTracking()
                .FirstOrDefaultAsync(f => f.ExternalId == payment.FeeExternalId && f.OwnerAdminId == payment.OwnerAdminId, ct);

        var lines = receipt is not null ? ReceiptLines(receipt) : [];
        if (lines.Count == 0 || Math.Abs(lines.Sum(l => l.Amount) - payment.Amount) > 0.01m)
            lines = [new ReceiptPdfLine(receipt is not null || fee is not null ? "Cuota de mantenimiento" : "Pago registrado", payment.Amount)];

        var model = await BuildModelAsync(
            number: payment.ExternalId,
            ownerAdminId: payment.OwnerAdminId,
            residentExternalId: payment.ResidentExternalId,
            concept: receipt?.Concept ?? (fee is not null ? "Cuota de mantenimiento" : "Pago de mantenimiento"),
            billingMonth: receipt?.EffectiveBillingMonth() ?? Period.Normalize(payment.FeeMonth) ?? payment.FeeMonth ?? fee?.Month,
            dueDate: receipt?.DueDate ?? fee?.DueDate,
            paidAtIso: payment.PaidAt,
            method: payment.Method ?? PaymentMethod.MercadoPago,
            reference: payment.Reference,
            lines: lines,
            total: payment.Amount,
            ct);

        return new ReceiptDocument($"recibo-{SafeFileName(payment.ExternalId)}.pdf", renderer.Render(model));
    }

    public async Task<ReceiptDocument> ForReceiptAsync(Receipt receipt, CancellationToken ct = default)
    {
        if (!receipt.IsPaid)
            throw new InvalidOperationException("El recibo aún no está pagado.");

        var suffix = $"-R-{receipt.ExternalId}";
        var payment = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.OwnerAdminId == receipt.OwnerAdminId && p.ExternalId.EndsWith(suffix))
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);

        var model = await BuildModelAsync(
            number: payment?.ExternalId ?? $"R-{receipt.ExternalId}",
            ownerAdminId: receipt.OwnerAdminId,
            residentExternalId: receipt.ResidentExternalId,
            concept: receipt.Concept ?? "Mantenimiento",
            billingMonth: receipt.EffectiveBillingMonth(),
            dueDate: receipt.DueDate,
            paidAtIso: payment?.PaidAt ?? receipt.UpdatedAt?.ToString("o"),
            method: payment?.Method ?? "Registrado por la administración",
            reference: payment?.Reference,
            lines: ReceiptLines(receipt),
            total: receipt.Total,
            ct);

        return new ReceiptDocument($"recibo-{SafeFileName(receipt.ExternalId)}.pdf", renderer.Render(model));
    }

    private static List<ReceiptPdfLine> ReceiptLines(Receipt r)
    {
        var lines = new List<ReceiptPdfLine> { new(r.Concept ?? "Cuota de mantenimiento", r.Amount) };
        if (r.LateFee > 0) lines.Add(new ReceiptPdfLine("Recargo por mora", r.LateFee));
        if (r.ExtraCharges > 0) lines.Add(new ReceiptPdfLine("Cargos adicionales", r.ExtraCharges));
        return lines;
    }

    private async Task<ReceiptPdfModel> BuildModelAsync(
        string number,
        int ownerAdminId,
        string residentExternalId,
        string concept,
        string? billingMonth,
        string? dueDate,
        string? paidAtIso,
        string method,
        string? reference,
        IReadOnlyList<ReceiptPdfLine> lines,
        decimal total,
        CancellationToken ct)
    {
        var admin = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerAdminId, ct);
        var resident = await db.Set<User>().AsNoTracking()
            .FirstOrDefaultAsync(u => u.ExternalId == residentExternalId && u.OwnerAdminId == ownerAdminId, ct);

        var unit = resident is null
            ? null
            : string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(resident.Floor) ? null : $"Piso {resident.Floor}",
                string.IsNullOrWhiteSpace(resident.Code) ? null : $"Depto {resident.Code}",
            }.Where(s => s is not null));

        return new ReceiptPdfModel(
            Number: number,
            BuildingName: FirstNonEmpty(admin?.Company, admin?.Name, "Condominio"),
            BuildingRuc: admin?.Ruc,
            BuildingAddress: admin?.Address,
            ResidentName: FirstNonEmpty(resident?.Name, residentExternalId),
            ResidentUnit: string.IsNullOrWhiteSpace(unit) ? null : unit,
            ResidentEmail: resident?.Email,
            Concept: concept,
            BillingMonth: MonthLabel(billingMonth),
            DueDate: DateLabel(dueDate),
            PaidAt: DateTimeLabel(paidAtIso),
            Method: method,
            Reference: reference,
            Lines: lines,
            Total: total,
            Currency: Money.Pen,
            IssuedAt: DateTimeLabel(DateTimeOffset.UtcNow.ToString("o")));
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string? MonthLabel(string? month)
    {
        if (!Period.TryParse(month, out var p)) return month;
        var label = p.FirstDay.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", Es);
        return char.ToUpper(label[0], Es) + label[1..];
    }

    private static string? DateLabel(string? ymd) =>
        MonthlyCharge.TryParseYmd(ymd, out var d) ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : ymd;

    private static string DateTimeLabel(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        // "yyyy-MM-dd" es una fecha local (Lima): leerla como medianoche UTC la mostraba el día anterior a las 19:00.
        if (iso.Trim().Length == 10 && MonthlyCharge.TryParseYmd(iso.Trim(), out var dateOnly))
            return dateOnly.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return iso;
        // Lima no tiene horario de verano: UTC−5 fijo.
        return at.ToOffset(TimeSpan.FromHours(-5)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    private static string SafeFileName(string value) =>
        new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
}
