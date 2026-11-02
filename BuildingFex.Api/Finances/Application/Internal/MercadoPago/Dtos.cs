namespace BuildingFex.Api.Finances.Application.Internal.MercadoPago;

/// <param name="ResidentExternalId">Si se indica, el recibo debe pertenecer a ese residente.</param>
public record CreatePreferenceRequest(
    string ReceiptExternalId,
    string OwnerAdminExternalId,
    string? ResidentExternalId = null);

public record CreateMaintenanceCheckoutRequest(
    string ResidentExternalId,
    string OwnerAdminExternalId,
    string? PayerEmail = null,
    string? FrontendBaseUrl = null);

public record ConfirmMaintenancePaymentRequest(
    string ResidentExternalId,
    string OwnerAdminExternalId,
    long? PaymentId,
    bool AllowDemo = false,
    string? CheckoutId = null);

public record MaintenancePaymentResult(bool Reconciled, int ItemsPaid, string? PaidAt, string Status = "approved");

/// <summary>Resultado del checkout: snapshot persistido (CheckoutId) + total calculado en el servidor.</summary>
public record MaintenanceCheckoutResult(
    string PreferenceId,
    string? InitPoint,
    string CheckoutId,
    decimal Amount,
    string ExternalReference);

/// <param name="DepartmentCount">Portafolio: departamentos facturables (S/4 por dpto., mínimo S/600). Se fija en la referencia.</param>
public record CreateSubscriptionPreferenceRequest(
    string AdminExternalId,
    string PlanId,
    string? PayerEmail = null,
    string? FrontendBaseUrl = null,
    int? DepartmentCount = null);

public record SubscriptionActivationResult(bool Activated, string PlanId, string? PaidUntil);

/// <param name="ExternalReference">Referencia enviada a MP (suscripción: incluye el id del snapshot de checkout).</param>
public record PreferenceResult(string PreferenceId, string? InitPoint, string? ExternalReference = null);

public record CardPaymentRequest(
    string Token,
    string PaymentMethodId,
    decimal TransactionAmount,
    int Installments,
    string PayerEmail,
    string ReceiptExternalId,
    string OwnerAdminExternalId,
    string? IssuerId = null,
    string? ResidentExternalId = null);

public record CardPaymentResult(
    string PaymentId,
    string Status,
    string StatusDetail,
    decimal TransactionAmount,
    string? PaymentMethodId,
    string? DateApproved);

public record WebhookResult(bool Processed, string Status);

/// <summary>Pagos simulados (demo) solicitados fuera de Development sin MercadoPago:AllowDemoPayments=true.</summary>
public class DemoPaymentsDisabledException()
    : InvalidOperationException("Los pagos de demostración están deshabilitados en este entorno.");
