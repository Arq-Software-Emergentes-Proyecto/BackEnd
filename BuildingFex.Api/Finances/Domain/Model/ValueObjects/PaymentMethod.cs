namespace BuildingFex.Api.Finances.Domain.Model.ValueObjects;

/// <summary>
/// Medios de pago (Tabla 60: MERCADO_PAGO, VOUCHER, BANK_TRANSFER).
/// Se guardan con su etiqueta visible porque payments.method se muestra tal cual en el historial.
/// </summary>
public static class PaymentMethod
{
    public const string MercadoPago = "Mercado Pago";
    public const string Voucher = "Voucher";
    public const string BankTransfer = "Transferencia bancaria";
    public const string Cash = "Efectivo";
}
