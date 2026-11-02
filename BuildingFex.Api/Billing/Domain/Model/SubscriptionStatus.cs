namespace BuildingFex.Api.Billing.Domain.Model;

/// <summary>Estado de la suscripción (informe, 5.4.1 — SubscriptionStatus).</summary>
public static class SubscriptionStatus
{
    /// <summary>Prueba Free de 30 días con las funciones de Essential.</summary>
    public const string Trial = "TRIAL";

    /// <summary>Plan de pago vigente.</summary>
    public const string Active = "ACTIVE";

    /// <summary>El último cobro falló (se mantiene el acceso mientras la junta regulariza).</summary>
    public const string PaymentFailed = "PAYMENT_FAILED";

    /// <summary>
    /// Sin plan vigente: la prueba venció o el periodo pagado terminó. Solo lectura para el padrón:
    /// la junta debe elegir un plan de pago para registrar nuevos residentes.
    /// </summary>
    public const string Suspended = "SUSPENDED";

    public const string Cancelled = "CANCELLED";

    public static bool IsKnown(string? value) =>
        value is Trial or Active or PaymentFailed or Suspended or Cancelled;
}
