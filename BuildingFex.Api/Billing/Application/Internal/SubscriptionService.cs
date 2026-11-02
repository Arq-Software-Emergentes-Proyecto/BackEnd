using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Billing.Domain.Model.Aggregates;
using BuildingFex.Api.Billing.Domain.Repositories;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Profiles.Domain.Repositories;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Billing.Application.Internal;

/// <summary>Resultado de comprobar si el condominio puede sumar residentes (RF028 / US028).</summary>
public sealed record RosterGrowthCheck(
    bool Allowed,
    string? ErrorCode,
    string? Message,
    int CurrentCount,
    int Limit,
    string PlanId);

/// <summary>Cobro aprobado de Mercado Pago a aplicar sobre la suscripción.</summary>
public sealed record SubscriptionPaymentInput(
    string MpPaymentId,
    string PlanId,
    int? DepartmentCount,
    decimal PaidAmount,
    string? Currency,
    decimal ExpectedAmount,
    string? CheckoutExternalId,
    string Source);

/// <summary>applied | already_processed | review_pending | amount_mismatch.</summary>
public sealed record SubscriptionPaymentOutcome(string Status, Subscription? Subscription, DateTimeOffset? PaidUntil)
{
    public const string Applied = "applied";
    public const string AlreadyProcessed = "already_processed";
    public const string ReviewPending = "review_pending";
    public const string AmountMismatch = "amount_mismatch";

    public bool Activated => Status == Applied;
}

/// <summary>
/// Casos de uso de Billing sobre la suscripción del condominio. Es la única vía de escritura del plan:
/// actualiza el agregado <see cref="Subscription"/> y el espejo legado en <c>users.subscription_*</c>.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>Suscripción del admin; si no existe (admin nuevo o anterior a la tabla) la crea.</summary>
    Task<Subscription> GetOrStartAsync(User admin, CancellationToken ct = default);

    /// <summary>Alta de un admin nuevo: prueba Free de 30 días desde ahora.</summary>
    Task<Subscription> StartTrialAsync(User admin, CancellationToken ct = default);

    Task<Subscription> ActivatePaidPlanAsync(
        User admin,
        string planId,
        DateTimeOffset paidUntil,
        int? billedDepartments,
        CancellationToken ct = default);

    Task<Subscription> ChangeToFreeAsync(User admin, CancellationToken ct = default);

    /// <summary>
    /// Aplica un cobro aprobado de MP una sola vez (mp_payment_id único): extiende paid_until desde
    /// máx(hoy, vencimiento vigente). Si el monto/moneda no coincide con <see cref="SubscriptionPaymentInput.ExpectedAmount"/>
    /// no activa nada y deja el cobro registrado para revisión manual. Un id ya registrado no vuelve a extender.
    /// </summary>
    Task<SubscriptionPaymentOutcome> ApplyPaymentAsync(User admin, SubscriptionPaymentInput input, CancellationToken ct = default);

    /// <summary>Snapshot del checkout (plan, departamentos y monto fijados al crear la preferencia).</summary>
    Task<SubscriptionCheckout> CreateCheckoutAsync(User admin, string planId, int? departmentCount, decimal amount, CancellationToken ct = default);

    Task<SubscriptionCheckout?> FindCheckoutAsync(string checkoutExternalId, CancellationToken ct = default);

    /// <summary>Departamentos facturables (Portafolio): máx(departamentos registrados en edificios, residentes).</summary>
    Task<int> CountBillableDepartmentsAsync(User admin, CancellationToken ct = default);

    /// <summary>¿Se pueden agregar <paramref name="additional"/> residentes? (límite del plan + prueba vigente).</summary>
    Task<RosterGrowthCheck> CheckRosterGrowthAsync(User admin, int additional, CancellationToken ct = default);

    /// <summary>Asocia el edificio principal a la suscripción (si aún no tenía uno).</summary>
    Task LinkBuildingAsync(User admin, int buildingId, CancellationToken ct = default);
}

public class SubscriptionService(
    ISubscriptionRepository subscriptionRepository,
    IUserRepository userRepository,
    IBuildingRepository buildingRepository,
    IUnitOfWork unitOfWork,
    ILogger<SubscriptionService> logger) : ISubscriptionService
{
    public const string ErrorPlanLimitReached = "RESIDENT_PLAN_LIMIT_REACHED";
    public const string ErrorTrialExpired = "SUBSCRIPTION_TRIAL_EXPIRED";
    public const string ErrorSubscriptionInactive = "SUBSCRIPTION_INACTIVE";

    public async Task<Subscription> GetOrStartAsync(User admin, CancellationToken ct = default)
    {
        admin = await AccountAsync(admin, ct);
        EnsureAdmin(admin);
        var existing = await subscriptionRepository.FindByOwnerAdminIdAsync(admin.Id, ct);
        if (existing is not null) return existing;

        // Admins anteriores a la tabla ya reciben su fila en SchemaUpgrade; aquí llegan los creados por
        // otras vías (seed, alta sin pasar por StartTrialAsync). Mismo criterio que el backfill:
        // plan de pago → ACTIVE; Free → prueba de gracia de 30 días desde hoy (users.created_at no es fiable).
        var created = Subscription.FromLegacy(
            admin.Id,
            admin.SubscriptionPlanId,
            admin.SubscriptionPaidUntil,
            DateTimeOffset.UtcNow);
        return await PersistNewAsync(created, admin.Id, ct);
    }

    public async Task<Subscription> StartTrialAsync(User admin, CancellationToken ct = default)
    {
        admin = await AccountAsync(admin, ct);
        EnsureAdmin(admin);
        var existing = await subscriptionRepository.FindByOwnerAdminIdAsync(admin.Id, ct);
        if (existing is not null) return existing;
        return await PersistNewAsync(Subscription.StartTrial(admin.Id, DateTimeOffset.UtcNow), admin.Id, ct);
    }

    public async Task<Subscription> ActivatePaidPlanAsync(
        User admin,
        string planId,
        DateTimeOffset paidUntil,
        int? billedDepartments,
        CancellationToken ct = default)
    {
        admin = await AccountAsync(admin, ct);
        var subscription = await GetOrStartAsync(admin, ct);
        subscription.Activate(planId, paidUntil, billedDepartments);
        subscriptionRepository.Update(subscription);

        admin.UpdateSubscription(subscription.PlanId, paidUntil);
        userRepository.Update(admin);
        await PropagateToPortfolioAsync(admin, ct);

        await unitOfWork.CompleteAsync(ct);
        return subscription;
    }

    public async Task<Subscription> ChangeToFreeAsync(User admin, CancellationToken ct = default)
    {
        admin = await AccountAsync(admin, ct);
        var subscription = await GetOrStartAsync(admin, ct);
        subscription.ChangeToFree();
        subscriptionRepository.Update(subscription);

        admin.UpdateSubscription(SubscriptionPlanIds.Free, null);
        userRepository.Update(admin);
        await PropagateToPortfolioAsync(admin, ct);

        await unitOfWork.CompleteAsync(ct);
        return subscription;
    }

    public async Task<int> CountBillableDepartmentsAsync(User admin, CancellationToken ct = default)
    {
        // Portafolio: se cobran los departamentos de todos los edificios de la cuenta.
        admin = await AccountAsync(admin, ct);
        var registered = 0;
        var residents = 0;
        foreach (var tenantId in await AccountTenantIdsAsync(admin, ct))
        {
            registered += await buildingRepository.SumDepartmentsByOwnerAdminIdAsync(tenantId, ct);
            residents += await userRepository.CountResidentsByOwnerAdminIdAsync(tenantId, ct);
        }

        return Math.Max(registered, residents);
    }

    public async Task<RosterGrowthCheck> CheckRosterGrowthAsync(User admin, int additional, CancellationToken ct = default)
    {
        admin = await AccountAsync(admin, ct);
        var subscription = await GetOrStartAsync(admin, ct);
        var now = DateTimeOffset.UtcNow;
        // Plan efectivo (espejo users.subscription_*, mismo criterio que el tope de IA): un plan de pago
        // vencido (pasada la gracia) no da sus topes.
        var planId = SubscriptionPlans.EffectivePlanId(admin.SubscriptionPlanId, admin.SubscriptionPaidUntil, now);
        var limit = SubscriptionPlans.MaxResidents(planId);
        // Portafolio: el límite de departamentos es de toda la cuenta (todos sus edificios).
        var current = 0;
        foreach (var tenantId in await AccountTenantIdsAsync(admin, ct))
            current += await userRepository.CountResidentsByOwnerAdminIdAsync(tenantId, ct);

        if (!subscription.AllowsRosterGrowth(now))
        {
            var expired = subscription.IsTrialExpired(now);
            return new RosterGrowthCheck(
                false,
                expired ? ErrorTrialExpired : ErrorSubscriptionInactive,
                expired
                    ? "La prueba gratuita de 30 días terminó. Elige un plan de pago en Ajustes → Planes para registrar residentes."
                    : subscription.IsPaidPeriodLapsed(now)
                        ? "El periodo pagado de tu plan venció. Renueva el pago en Ajustes → Planes para registrar residentes."
                        : "La suscripción no está activa. Elige un plan en Ajustes → Planes para registrar residentes.",
                current,
                limit,
                planId);
        }

        if (current + Math.Max(0, additional) > limit)
        {
            return new RosterGrowthCheck(
                false,
                ErrorPlanLimitReached,
                $"Límite del plan alcanzado ({current}/{limit} departamentos). Mejora tu plan en Ajustes → Planes.",
                current,
                limit,
                planId);
        }

        return new RosterGrowthCheck(true, null, null, current, limit, planId);
    }

    public async Task<SubscriptionPaymentOutcome> ApplyPaymentAsync(
        User admin,
        SubscriptionPaymentInput input,
        CancellationToken ct = default)
    {
        EnsureAdmin(admin);
        admin = await AccountAsync(admin, ct);
        var mpId = (input.MpPaymentId ?? string.Empty).Trim();
        if (mpId.Length == 0)
            throw new InvalidOperationException("A Mercado Pago payment id is required.");

        var existing = await subscriptionRepository.FindPaymentByMpIdAsync(mpId, ct);
        if (existing is not null)
            return Replayed(existing);

        var planId = SubscriptionPlans.Normalize(input.PlanId);
        var amountOk = Math.Abs(input.PaidAmount - input.ExpectedAmount) <= 0.01m
                       && (string.IsNullOrWhiteSpace(input.Currency)
                           || string.Equals(input.Currency.Trim(), "PEN", StringComparison.OrdinalIgnoreCase));

        if (!amountOk || !SubscriptionPlans.IsPaid(planId))
        {
            var review = SubscriptionPayment.Create(
                mpId, admin.Id, planId, input.PaidAmount, input.ExpectedAmount, input.Currency,
                input.DepartmentCount, input.CheckoutExternalId,
                SubscriptionPaymentStatus.ReviewAmountMismatch, input.Source, null);
            await subscriptionRepository.AddPaymentAsync(review, ct);
            try
            {
                await unitOfWork.CompleteAsync(ct);
            }
            catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex, BillingIndexes.SubscriptionPaymentMpId))
            {
                subscriptionRepository.Detach(review);
                var raced = await subscriptionRepository.FindPaymentByMpIdAsync(mpId, ct);
                return raced is null ? new(SubscriptionPaymentOutcome.AlreadyProcessed, null, null) : Replayed(raced);
            }

            logger.LogWarning(
                "Subscription payment MP {MpId} for admin {AdminId} plan {PlanId}: paid {Paid} {Currency}, expected {Expected} — saved for manual review.",
                mpId, admin.ExternalId, planId, input.PaidAmount, input.Currency, input.ExpectedAmount);
            return new SubscriptionPaymentOutcome(SubscriptionPaymentOutcome.AmountMismatch, null, null);
        }

        var subscription = await GetOrStartAsync(admin, ct);
        var now = DateTimeOffset.UtcNow;
        var paidUntil = subscription.NextPaidUntil(now);
        subscription.Activate(planId, paidUntil, input.DepartmentCount);
        subscriptionRepository.Update(subscription);
        admin.UpdateSubscription(subscription.PlanId, paidUntil);
        userRepository.Update(admin);
        await PropagateToPortfolioAsync(admin, ct);

        var payment = SubscriptionPayment.Create(
            mpId, admin.Id, planId, input.PaidAmount, input.ExpectedAmount, input.Currency,
            input.DepartmentCount, input.CheckoutExternalId,
            SubscriptionPaymentStatus.Applied, input.Source, paidUntil);
        await subscriptionRepository.AddPaymentAsync(payment, ct);

        var checkout = string.IsNullOrWhiteSpace(input.CheckoutExternalId)
            ? null
            : await subscriptionRepository.FindCheckoutAsync(input.CheckoutExternalId, ct);
        if (checkout is not null && checkout.OwnerAdminId == admin.Id)
        {
            checkout.MarkPaid(mpId);
        }

        try
        {
            await unitOfWork.CompleteAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex, BillingIndexes.SubscriptionPaymentMpId))
        {
            // confirm + webhook simultáneos: el otro request ya aplicó el pago. Se descartan los cambios en memoria.
            subscriptionRepository.Detach(payment);
            await subscriptionRepository.ReloadAsync(subscription, ct);
            await subscriptionRepository.ReloadAsync(admin, ct);
            if (checkout is not null) await subscriptionRepository.ReloadAsync(checkout, ct);
            logger.LogInformation("Subscription payment MP {MpId} applied concurrently; skipping.", mpId);
            return new SubscriptionPaymentOutcome(SubscriptionPaymentOutcome.AlreadyProcessed, subscription, subscription.PaidUntil);
        }

        return new SubscriptionPaymentOutcome(SubscriptionPaymentOutcome.Applied, subscription, paidUntil);
    }

    public async Task<SubscriptionCheckout> CreateCheckoutAsync(
        User admin,
        string planId,
        int? departmentCount,
        decimal amount,
        CancellationToken ct = default)
    {
        EnsureAdmin(admin);
        admin = await AccountAsync(admin, ct);
        var checkout = SubscriptionCheckout.Create(admin.Id, planId, departmentCount, amount);
        await subscriptionRepository.AddCheckoutAsync(checkout, ct);
        await unitOfWork.CompleteAsync(ct);
        return checkout;
    }

    public Task<SubscriptionCheckout?> FindCheckoutAsync(string checkoutExternalId, CancellationToken ct = default) =>
        subscriptionRepository.FindCheckoutAsync(checkoutExternalId, ct);

    private static SubscriptionPaymentOutcome Replayed(SubscriptionPayment existing) =>
        existing.IsApplied
            ? new SubscriptionPaymentOutcome(SubscriptionPaymentOutcome.AlreadyProcessed, null, existing.PaidUntil)
            : new SubscriptionPaymentOutcome(SubscriptionPaymentOutcome.ReviewPending, null, null);

    public async Task LinkBuildingAsync(User admin, int buildingId, CancellationToken ct = default)
    {
        // Los edificios de Portafolio no cambian el edificio principal de la suscripción de la matriz.
        if (admin.ParentAdminId.HasValue) return;
        var subscription = await GetOrStartAsync(admin, ct);
        if (subscription.BuildingId.HasValue) return;
        subscription.LinkBuilding(buildingId);
        subscriptionRepository.Update(subscription);
        await unitOfWork.CompleteAsync(ct);
    }

    private async Task<Subscription> PersistNewAsync(Subscription subscription, int ownerAdminId, CancellationToken ct)
    {
        await subscriptionRepository.AddAsync(subscription, ct);
        try
        {
            await unitOfWork.CompleteAsync(ct);
            return subscription;
        }
        catch (DbUpdateException ex)
        {
            // Dos requests simultáneos crearon la fila (UNIQUE owner_admin_id): usar la que quedó.
            logger.LogInformation(ex, "Subscription for admin {AdminId} created concurrently; reloading.", ownerAdminId);
            subscriptionRepository.Remove(subscription);
            var reloaded = await subscriptionRepository.FindByOwnerAdminIdAsync(ownerAdminId, ct);
            return reloaded ?? throw new InvalidOperationException("Subscription could not be created.", ex);
        }
    }

    /// <summary>
    /// Cuenta que paga la suscripción. Un edificio adicional de Portafolio (cuenta técnica) comparte la
    /// suscripción, los topes y el cobro de su cuenta matriz.
    /// </summary>
    private async Task<User> AccountAsync(User admin, CancellationToken ct)
    {
        if (!admin.ParentAdminId.HasValue) return admin;
        return admin.ParentAdmin
               ?? await userRepository.FindByIdAsync(admin.ParentAdminId.Value, ct)
               ?? admin;
    }

    /// <summary>Copia el plan vigente de la cuenta matriz a sus edificios (espejo users.subscription_*).</summary>
    private async Task PropagateToPortfolioAsync(User account, CancellationToken ct)
    {
        foreach (var building in await userRepository.ListPortfolioBuildingsAsync(account.Id, ct))
        {
            building.UpdateSubscription(account.SubscriptionPlanId, account.SubscriptionPaidUntil);
            userRepository.Update(building);
        }
    }

    /// <summary>Ids internos de los condominios de la cuenta: la matriz y sus edificios de Portafolio.</summary>
    private async Task<IReadOnlyList<int>> AccountTenantIdsAsync(User account, CancellationToken ct)
    {
        var buildings = await userRepository.ListPortfolioBuildingsAsync(account.Id, ct);
        return [account.Id, .. buildings.Select(b => b.Id)];
    }

    private static void EnsureAdmin(User admin)
    {
        if (admin.Role != "admin")
            throw new InvalidOperationException("Only admin accounts have subscriptions.");
    }
}
