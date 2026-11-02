using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Billing.Application.Internal;
using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Billing.Interfaces.Rest.Resources;
using BuildingFex.Api.Finances.Application.Internal.MercadoPago;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Iam.Interfaces.Rest.Resources;
using BuildingFex.Api.Profiles.Domain.Repositories;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Billing.Interfaces.Rest;

/// <summary>
/// Suscripción del condominio. El tenant sale siempre del token (CallerContext): un admin solo ve y
/// cambia la suscripción de su propio condominio; los residentes solo pueden leer los topes (/limits).
/// </summary>
[ApiController]
[Route("api/v1/subscription")]
[Authorize]
public class SubscriptionController(
    IUserRepository userRepository,
    IBuildingRepository buildingRepository,
    ISubscriptionService subscriptionService,
    IMercadoPagoService mercadoPagoService,
    ILogger<SubscriptionController> logger) : TenantScopedControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetCurrent(CancellationToken ct)
    {
        var (admin, error) = await ResolveAdminAsync(ct);
        if (admin is null) return error!;

        return Ok(await BuildResponseAsync(admin, ct));
    }

    /// <summary>Topes vigentes del condominio (departamentos y consultas de IA). Admin o residente del tenant.</summary>
    [HttpGet("limits")]
    [Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
    public async Task<IActionResult> GetLimits(CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return tenant.Error;
        var admin = tenant.Caller!.TenantAdmin;

        var subscription = await subscriptionService.GetOrStartAsync(admin, ct);
        var now = DateTimeOffset.UtcNow;
        var limits = PlanLimitsFor(admin);
        var used = await userRepository.CountResidentsByOwnerAdminIdAsync(admin.Id, ct);

        return Ok(new SubscriptionLimitsResource(
            SubscriptionPlans.Normalize(admin.SubscriptionPlanId),
            subscription.EffectiveStatus(now),
            limits.MaxDepartments,
            used,
            Math.Max(0, limits.MaxDepartments - used),
            limits.MonthlyAiQueries,
            // El conteo de consultas de IA lo lleva Resident Care; aquí solo se publica el tope.
            null,
            subscription.AllowsRosterGrowth(now) && used < limits.MaxDepartments));
    }

    [HttpPatch]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> ChangePlan(
        [FromBody] ChangeSubscriptionPlanResource resource,
        CancellationToken ct)
    {
        var (admin, error) = await ResolveAdminAsync(ct);
        if (admin is null) return error!;

        var planId = SubscriptionPlans.Normalize(resource.PlanId);
        if (SubscriptionPlans.IsPaid(planId))
            return BadRequest(new
            {
                code = "PAID_PLAN_REQUIRES_CHECKOUT",
                message = "Los planes de pago requieren checkout con Mercado Pago.",
            });

        var residentsCount = await userRepository.CountResidentsByOwnerAdminIdAsync(admin.Id, ct);
        var newLimit = SubscriptionPlans.MaxResidents(planId);
        if (residentsCount > newLimit)
        {
            return BadRequest(new
            {
                code = "PLAN_DOWNGRADE_RESIDENTS_EXCEEDED",
                message = $"Tienes {residentsCount} residentes; el plan {planId} permite máximo {newLimit}.",
                residentsCount,
                residentLimit = newLimit,
            });
        }

        // Volver a Free no reinicia la prueba de 30 días (si ya venció, queda SUSPENDED).
        await subscriptionService.ChangeToFreeAsync(admin, ct);
        return Ok(await BuildResponseAsync(admin, ct));
    }

    [HttpPost("checkout")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Checkout(
        [FromBody] SubscriptionCheckoutResource resource,
        CancellationToken ct)
    {
        var (admin, error) = await ResolveAdminAsync(ct);
        if (admin is null) return error!;

        var planId = SubscriptionPlans.Normalize(resource.PlanId);
        if (!SubscriptionPlans.IsPaid(planId))
            return BadRequest(new { code = "FREE_PLAN_NO_CHECKOUT", message = "El plan free no requiere pago." });

        var residentsCount = await userRepository.CountResidentsByOwnerAdminIdAsync(admin.Id, ct);
        var planLimit = SubscriptionPlans.MaxResidents(planId);
        if (residentsCount > planLimit)
        {
            return BadRequest(new
            {
                code = "PLAN_DOWNGRADE_RESIDENTS_EXCEEDED",
                message = $"Tienes {residentsCount} residentes; el plan {planId} permite máximo {planLimit}.",
                residentsCount,
                residentLimit = planLimit,
            });
        }

        // Portafolio: S/4 por departamento (mínimo S/600). El número queda fijado en la referencia del pago,
        // y confirm / webhook verifican contra ese mismo monto.
        int? departments = planId == SubscriptionPlanIds.Portafolio
            ? await subscriptionService.CountBillableDepartmentsAsync(admin, ct)
            : null;
        var amount = MercadoPagoService.ExpectedSubscriptionAmount(planId, departments);

        try
        {
            var frontendBaseUrl = resource.FrontendBaseUrl;
            if (string.IsNullOrWhiteSpace(frontendBaseUrl))
            {
                frontendBaseUrl = Request.Headers.Origin.FirstOrDefault()
                    ?? Request.Headers.Referer.FirstOrDefault();
            }

            var result = await mercadoPagoService.CreateSubscriptionPreferenceAsync(
                new CreateSubscriptionPreferenceRequest(
                    admin.ExternalId,
                    planId,
                    admin.Email,
                    frontendBaseUrl,
                    departments),
                ct);

            var demo = string.Equals(result.PreferenceId, "DEMO", StringComparison.OrdinalIgnoreCase);
            return Ok(new
            {
                preferenceId = result.PreferenceId,
                initPoint = result.InitPoint,
                demo,
                amount,
                planId,
                departmentCount = departments,
                externalReference = result.ExternalReference
                    ?? MercadoPagoService.BuildSubscriptionExternalReference(admin.ExternalId, planId, departments),
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Subscription checkout rejected for admin {AdminId} plan {PlanId}", admin.ExternalId, planId);
            return BadRequest(new { code = "CHECKOUT_ERROR", message = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subscription checkout failed for admin {AdminId} plan {PlanId}", admin.ExternalId, planId);
            var detail = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { code = "CHECKOUT_ERROR", message = detail });
        }
    }

    [HttpPost("confirm")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Confirm(
        [FromBody] ConfirmSubscriptionResource resource,
        CancellationToken ct)
    {
        var (admin, error) = await ResolveAdminAsync(ct);
        if (admin is null) return error!;

        var planId = SubscriptionPlans.Normalize(resource.PlanId);

        try
        {
            var result = await mercadoPagoService.ConfirmSubscriptionPaymentAsync(
                admin.ExternalId,
                planId,
                resource.PaymentId,
                resource.Demo,
                ct);

            var refreshed = await userRepository.FindByExternalIdAsync(admin.ExternalId, ct);
            return Ok(new
            {
                activated = result.Activated,
                planId = result.PlanId,
                paidUntil = result.PaidUntil,
                subscription = refreshed is null ? null : await BuildResponseAsync(refreshed, ct),
            });
        }
        catch (DemoPaymentsDisabledException ex)
        {
            logger.LogWarning("Demo subscription confirm rejected for admin {AdminId}", admin.ExternalId);
            return BadRequest(new { code = "DEMO_PAYMENTS_DISABLED", message = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Subscription confirm failed for admin {AdminId}", admin.ExternalId);
            return BadRequest(new { code = "CONFIRM_FAILED", message = ex.Message });
        }
    }

    /// <summary>
    /// Cuenta que paga la suscripción del condominio activo (la matriz en Portafolio), resuelta desde el token.
    /// Ver el plan: quien gestiona el condominio (incluida la junta). Cambiarlo o pagarlo: solo el dueño de la cuenta.
    /// </summary>
    private async Task<(User? Admin, IActionResult? Error)> ResolveAdminAsync(CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return (null, tenant.Error);
        var caller = tenant.Caller!;
        if (!caller.IsAdmin)
            return (null, StatusCode(StatusCodes.Status403Forbidden, new { code = "ADMIN_REQUIRED", message = "Solo administradores." }));
        if (!HttpMethods.IsGet(Request.Method) && caller.TenantRole != TenantRoles.Owner)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "ACCOUNT_OWNER_REQUIRED",
                message = "Solo el dueño de la cuenta puede cambiar o pagar el plan.",
            }));
        }

        return (caller.BillingAdmin, null);
    }

    /// <summary>Topes del plan efectivo: un plan de pago vencido (pasada la gracia) rige con los de Free.</summary>
    private static Domain.Model.ValueObjects.PlanLimits PlanLimitsFor(User admin) =>
        Domain.Model.ValueObjects.PlanLimits.For(
            SubscriptionPlans.EffectivePlanId(admin.SubscriptionPlanId, admin.SubscriptionPaidUntil, DateTimeOffset.UtcNow));

    private async Task<SubscriptionResource> BuildResponseAsync(User admin, CancellationToken ct)
    {
        var subscription = await subscriptionService.GetOrStartAsync(admin, ct);
        var now = DateTimeOffset.UtcNow;
        var planId = SubscriptionPlans.Normalize(admin.SubscriptionPlanId);
        var residentsCount = await userRepository.CountResidentsByOwnerAdminIdAsync(admin.Id, ct);
        var billable = await subscriptionService.CountBillableDepartmentsAsync(admin, ct);
        var limits = PlanLimitsFor(admin);

        var buildings = await buildingRepository.ListByOwnerAdminIdAsync(admin.Id, ct);
        var buildingId = subscription.BuildingId ?? buildings.FirstOrDefault()?.Id;
        var suggestionBase = buildings.Count > 0 ? Math.Max(billable, buildings.Sum(b => b.DepartmentCount)) : Math.Max(1, residentsCount);

        return new SubscriptionResource(
            planId,
            limits.MaxDepartments,
            residentsCount,
            planId == SubscriptionPlanIds.Portafolio
                ? MercadoPagoService.ExpectedSubscriptionAmount(planId, subscription.BilledDepartments ?? billable)
                : SubscriptionPlans.MonthlyPricePen(planId),
            admin.SubscriptionPaidUntil?.ToString("o"),
            SubscriptionPlans.IsPaid(planId),
            subscription.EffectiveStatus(now),
            subscription.TrialEndsAt?.ToString("o"),
            subscription.TrialDaysLeft(now),
            subscription.IsTrialExpired(now),
            subscription.RequiresPlanSelection(now),
            subscription.AllowsRosterGrowth(now) && residentsCount < limits.MaxDepartments,
            limits.MaxDepartments,
            limits.MonthlyAiQueries,
            billable,
            buildingId,
            PlanCatalog.SuggestForDepartments(suggestionBase));
    }
}
