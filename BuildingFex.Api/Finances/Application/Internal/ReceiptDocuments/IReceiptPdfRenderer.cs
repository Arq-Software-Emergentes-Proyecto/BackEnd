namespace BuildingFex.Api.Finances.Application.Internal.ReceiptDocuments;

/// <summary>Línea de detalle del comprobante (concepto → monto).</summary>
public sealed record ReceiptPdfLine(string Label, decimal Amount);

/// <summary>Datos del comprobante de pago descargable (RF022 / US022: "emite el recibo descargable").</summary>
public sealed record ReceiptPdfModel(
    string Number,
    string BuildingName,
    string? BuildingRuc,
    string? BuildingAddress,
    string ResidentName,
    string? ResidentUnit,
    string? ResidentEmail,
    string Concept,
    string? BillingMonth,
    string? DueDate,
    string PaidAt,
    string Method,
    string? Reference,
    IReadOnlyList<ReceiptPdfLine> Lines,
    decimal Total,
    string Currency,
    string IssuedAt);

public interface IReceiptPdfRenderer
{
    byte[] Render(ReceiptPdfModel model);
}
