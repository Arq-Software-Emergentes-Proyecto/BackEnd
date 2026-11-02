using BuildingFex.Api.Billing.Domain.Model.ValueObjects;
using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Billing.Domain.Model.Aggregates;

/// <summary>
/// Suscripción del condominio (tenant = admin dueño) a un plan de BuildingFex (informe, 5.4.1).
/// Tabla <c>subscriptions</c>, 1:1 con el admin (<c>owner_admin_id</c> → users.id).
/// <para>
/// Compatibilidad: <c>users.subscription_plan_id</c> / <c>users.subscription_paid_until</c> se siguen
/// escribiendo como espejo (los leen IAM y Resident Care para límites y topes de IA). La única vía de
/// escritura es <see cref="Application.Internal.ISubscriptionService"/>, que actualiza ambos en el mismo SaveChanges.
/// </para>
/// </summary>
public class Subscription : IAuditableEntity
{
    public int Id { get; private set; }

    /// <summary>Tenant: users.id del admin dueño del condominio.</summary>
    public int OwnerAdminId { get; private set; }

    /// <summary>Edificio principal (Profiles.buildings.id) si ya se registró.</summary>
    public int? BuildingId { get; private set; }

    public string PlanId { get; private set; } = SubscriptionPlanIds.Free;

    /// <summary>Estado persistido (<see cref="SubscriptionStatus"/>). Ver <see cref="EffectiveStatus"/>.</summary>
    public string Status { get; private set; } = SubscriptionStatus.Trial;

    public DateTimeOffset? TrialEndsAt { get; private set; }
    public DateTimeOffset? PaidUntil { get; private set; }
    public int FailedCharges { get; private set; }

    /// <summary>Departamentos facturados en el último cobro (Portafolio cobra S/4 por dpto.).</summary>
    public int? BilledDepartments { get; private set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private Subscription()
    {
    }

    /// <summary>Prueba Free de 30 días desde <paramref name="startedAt"/> (alta de un admin nuevo).</summary>
    public static Subscription StartTrial(int ownerAdminId, DateTimeOffset startedAt)
    {
        var now = DateTimeOffset.UtcNow;
        return new Subscription
        {
            OwnerAdminId = ownerAdminId,
            PlanId = SubscriptionPlanIds.Free,
            Status = SubscriptionStatus.Trial,
            TrialEndsAt = startedAt.AddDays(SubscriptionPlans.FreeTrialDays),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Backfill de admins existentes antes de esta tabla: un plan de pago queda ACTIVE con su paidUntil;
    /// un plan Free recibe una prueba de gracia de 30 días desde <paramref name="graceStart"/> (no se bloquea a nadie al desplegar).
    /// </summary>
    public static Subscription FromLegacy(int ownerAdminId, string? planId, DateTimeOffset? paidUntil, DateTimeOffset graceStart)
    {
        var normalized = SubscriptionPlans.Normalize(planId);
        if (!SubscriptionPlans.IsPaid(normalized))
            return StartTrial(ownerAdminId, graceStart);

        var now = DateTimeOffset.UtcNow;
        return new Subscription
        {
            OwnerAdminId = ownerAdminId,
            PlanId = normalized,
            Status = SubscriptionStatus.Active,
            PaidUntil = paidUntil,
            TrialEndsAt = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Cobro aprobado: activa (o renueva) un plan de pago.</summary>
    public void Activate(string planId, DateTimeOffset paidUntil, int? billedDepartments = null)
    {
        var normalized = SubscriptionPlans.Normalize(planId);
        if (!SubscriptionPlans.IsPaid(normalized))
            throw new InvalidOperationException("Only paid plans can be activated with a charge.");

        PlanId = normalized;
        Status = SubscriptionStatus.Active;
        PaidUntil = paidUntil;
        FailedCharges = 0;
        BilledDepartments = billedDepartments;
        Touch();
    }

    /// <summary>
    /// Volver al plan Free. No reinicia la prueba: si ya venció, queda SUSPENDED hasta elegir un plan de pago.
    /// </summary>
    public void ChangeToFree()
    {
        PlanId = SubscriptionPlanIds.Free;
        PaidUntil = null;
        BilledDepartments = null;
        Status = SubscriptionStatus.Trial;
        TrialEndsAt ??= (CreatedAt ?? DateTimeOffset.UtcNow).AddDays(SubscriptionPlans.FreeTrialDays);
        Touch();
    }

    public void RegisterFailedCharge()
    {
        FailedCharges++;
        if (Status == SubscriptionStatus.Active)
            Status = SubscriptionStatus.PaymentFailed;
        Touch();
    }

    public void Cancel()
    {
        Status = SubscriptionStatus.Cancelled;
        Touch();
    }

    public void LinkBuilding(int buildingId)
    {
        if (BuildingId == buildingId) return;
        BuildingId = buildingId;
        Touch();
    }

    public bool IsTrial => Status == SubscriptionStatus.Trial;

    public bool IsTrialExpired(DateTimeOffset now) =>
        Status == SubscriptionStatus.Trial && TrialEndsAt.HasValue && TrialEndsAt.Value <= now;

    /// <summary>Plan de pago cuyo periodo pagado ya terminó (no hay cobro recurrente automático todavía).</summary>
    public bool IsPaidPeriodOver(DateTimeOffset now) =>
        SubscriptionPlans.IsPaid(PlanId) && PaidUntil.HasValue && PaidUntil.Value <= now;

    /// <summary>Periodo pagado vencido y fuera de la gracia de <see cref="SubscriptionPlans.PaidGraceDays"/> días.</summary>
    public bool IsPaidPeriodLapsed(DateTimeOffset now) =>
        SubscriptionPlans.IsPaidPeriodLapsed(PlanId, PaidUntil, now);

    /// <summary>Plan cuyos topes rigen hoy: Free si el plan de pago venció (pasada la gracia).</summary>
    public string EffectivePlanId(DateTimeOffset now) =>
        SubscriptionPlans.EffectivePlanId(PlanId, PaidUntil, now);

    /// <summary>
    /// Estado efectivo a la fecha: una prueba vencida se informa como SUSPENDED (equivale al evento PruebaVencida
    /// del ExpireTrialsJob, calculado al leer en lugar de con un job). Un plan de pago vencido queda PAYMENT_FAILED
    /// durante la gracia y SUSPENDED después.
    /// </summary>
    public string EffectiveStatus(DateTimeOffset now)
    {
        if (IsTrialExpired(now)) return SubscriptionStatus.Suspended;
        if (Status is SubscriptionStatus.Active or SubscriptionStatus.PaymentFailed)
        {
            if (IsPaidPeriodLapsed(now)) return SubscriptionStatus.Suspended;
            if (IsPaidPeriodOver(now)) return SubscriptionStatus.PaymentFailed;
        }
        return Status;
    }

    /// <summary>La junta debe elegir un plan de pago (prueba vencida, cancelada o renovación pendiente).</summary>
    public bool RequiresPlanSelection(DateTimeOffset now) =>
        IsTrialExpired(now)
        || Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled
        || IsPaidPeriodOver(now);

    /// <summary>
    /// ¿Se puede ampliar el padrón (crear / importar residentes)? Se bloquea con la prueba vencida, suspendida,
    /// cancelada o con el plan de pago vencido (pasada la gracia). Nunca se bloquean lecturas.
    /// </summary>
    public bool AllowsRosterGrowth(DateTimeOffset now) =>
        !IsTrialExpired(now)
        && !IsPaidPeriodLapsed(now)
        && Status is not (SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled);

    /// <summary>Nuevo vencimiento tras un cobro: extiende desde máx(hoy, vencimiento vigente).</summary>
    public DateTimeOffset NextPaidUntil(DateTimeOffset now)
    {
        var from = SubscriptionPlans.IsPaid(PlanId) && PaidUntil is { } until && until > now ? until : now;
        return from.AddDays(SubscriptionPlans.PaidPeriodDays);
    }

    public int? TrialDaysLeft(DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Trial || !TrialEndsAt.HasValue) return null;
        var days = (int)Math.Ceiling((TrialEndsAt.Value - now).TotalDays);
        return Math.Max(0, days);
    }

    public PlanLimits Limits() => PlanLimits.For(PlanId);

    /// <summary>Topes vigentes (plan efectivo: Free si el plan de pago venció).</summary>
    public PlanLimits EffectiveLimits(DateTimeOffset now) => PlanLimits.For(EffectivePlanId(now));

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}
