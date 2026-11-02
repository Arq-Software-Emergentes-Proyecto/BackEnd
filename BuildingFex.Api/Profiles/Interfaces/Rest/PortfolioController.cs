using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Profiles.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IamUser = BuildingFex.Api.Iam.Domain.Model.Aggregates.User;

namespace BuildingFex.Api.Profiles.Interfaces.Rest;

/// <summary>
/// La plataforma dividida por edificios. Cada edificio es un condominio con sus datos aislados
/// (residentes, cuotas, reservas, incidencias…). Una persona puede tener acceso a varios: el dueño de una
/// cuenta Portafolio a todos sus edificios, y un administrador asignado o la junta a los que los invitaron.
/// El cliente elige el edificio activo con la cabecera X-Building-Id.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class PortfolioController(AppDbContext db, ICallerContextAccessor callers) : ControllerBase
{
    /// <summary>Edificios a los que tiene acceso quien llama, con su rol en cada uno.</summary>
    [HttpGet("api/v1/me/buildings")]
    public async Task<IActionResult> MyBuildings(CancellationToken ct)
    {
        var caller = await callers.GetAsync(ct);
        if (caller is null) return TenantRequired();

        var tenantIds = caller.AccessibleTenants.Select(t => t.Tenant.Id).ToList();
        var buildings = await db.Set<Building>().AsNoTracking()
            .Where(b => tenantIds.Contains(b.OwnerAdminId))
            .OrderBy(b => b.Id)
            .ToListAsync(ct);

        var account = caller.User.Role == UserRoles.Admin && !caller.User.IsManagedBuildingAccount ? caller.User : null;
        var accountPlan = account is null
            ? null
            : SubscriptionPlans.EffectivePlanId(account.SubscriptionPlanId, account.SubscriptionPaidUntil, DateTimeOffset.UtcNow);

        return Ok(new
        {
            activeOwnerAdminId = caller.TenantExternalId,
            accountKind = account?.AccountKind ?? (account is null ? null : IamUser.AccountKindAdministrator),
            // Agregar edificios: solo el dueño de una cuenta Portafolio vigente que ya registró su primer edificio.
            canAddBuildings = accountPlan == SubscriptionPlanIds.Portafolio
                              && buildings.Any(b => b.OwnerAdminId == account!.Id),
            planId = accountPlan,
            buildings = caller.AccessibleTenants.Select(t =>
            {
                var b = buildings.FirstOrDefault(x => x.OwnerAdminId == t.Tenant.Id);
                return new
                {
                    ownerAdminId = t.Tenant.ExternalId,
                    buildingId = b?.Id,
                    name = b?.Name ?? t.Tenant.Name,
                    address = b?.Address,
                    departmentCount = b?.DepartmentCount,
                    role = t.Role,
                    isPortfolioBuilding = t.Tenant.IsManagedBuildingAccount,
                    isActive = t.Tenant.Id == caller.TenantId,
                };
            }),
        });
    }

    /// <summary>Consola Portafolio: resumen de cada edificio de la cuenta (residentes e incidencias abiertas).</summary>
    [HttpGet("api/v1/portfolio/buildings")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var caller = await callers.GetAsync(ct);
        if (caller is null) return TenantRequired();

        var tenantIds = caller.AccessibleTenants.Select(t => t.Tenant.Id).ToList();
        var buildings = await db.Set<Building>().AsNoTracking()
            .Where(b => tenantIds.Contains(b.OwnerAdminId))
            .ToListAsync(ct);
        var residents = await db.Set<User>().AsNoTracking()
            .Where(u => u.Role == UserRoles.Resident && u.OwnerAdminId != null && tenantIds.Contains(u.OwnerAdminId.Value))
            .GroupBy(u => u.OwnerAdminId!.Value)
            .Select(g => new { Tenant = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var openIncidents = await db.Set<Incident>().AsNoTracking()
            .Where(i => tenantIds.Contains(i.OwnerAdminId) && i.Status != IncidentStatuses.Resolved)
            .GroupBy(i => i.OwnerAdminId)
            .Select(g => new { Tenant = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return Ok(caller.AccessibleTenants.Select(t =>
        {
            var b = buildings.Where(x => x.OwnerAdminId == t.Tenant.Id).OrderBy(x => x.Id).FirstOrDefault();
            return new
            {
                ownerAdminId = t.Tenant.ExternalId,
                buildingId = b?.Id,
                name = b?.Name ?? t.Tenant.Name,
                address = b?.Address,
                departmentCount = b?.DepartmentCount ?? 0,
                residents = residents.FirstOrDefault(r => r.Tenant == t.Tenant.Id)?.Count ?? 0,
                openIncidents = openIncidents.FirstOrDefault(r => r.Tenant == t.Tenant.Id)?.Count ?? 0,
                role = t.Role,
                isPortfolioBuilding = t.Tenant.IsManagedBuildingAccount,
            };
        }));
    }

    /// <summary>
    /// Portafolio: registra otro edificio en la cuenta. Queda como un condominio aparte (sus datos no se mezclan
    /// con los de los demás), administrado por la misma cuenta y cobrado en la misma suscripción.
    /// </summary>
    [HttpPost("api/v1/portfolio/buildings")]
    public async Task<IActionResult> Create([FromBody] UpsertBuildingResource? resource, CancellationToken ct)
    {
        var caller = await callers.GetAsync(ct);
        if (caller is null) return TenantRequired();

        var account = caller.User;
        if (account.Role != UserRoles.Admin || account.IsManagedBuildingAccount)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "ACCOUNT_OWNER_REQUIRED",
                message = "Solo el dueño de la cuenta puede agregar edificios.",
            });

        var plan = SubscriptionPlans.EffectivePlanId(account.SubscriptionPlanId, account.SubscriptionPaidUntil, DateTimeOffset.UtcNow);
        if (plan != SubscriptionPlanIds.Portafolio)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "PORTFOLIO_PLAN_REQUIRED",
                message = "Para administrar varios edificios en la misma cuenta contrata el plan Portafolio.",
            });
        }

        if (!await db.Set<Building>().AnyAsync(b => b.OwnerAdminId == account.Id, ct))
        {
            return Conflict(new
            {
                code = "FIRST_BUILDING_REQUIRED",
                message = "Registra primero el edificio principal de la cuenta.",
            });
        }

        var errors = Building.Validate(resource?.Name, resource?.Address, resource?.DepartmentCount);
        if (errors.Count > 0)
        {
            var problem = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Datos del edificio inválidos.",
            };
            problem.Extensions["code"] = "BUILDING_VALIDATION_FAILED";
            return BadRequest(problem);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var buildingAccount = IamUser.CreateManagedBuildingAccount(account, resource!.Name!);
        db.Set<User>().Add(buildingAccount);
        await db.SaveChangesAsync(ct);

        var building = Building.Register(buildingAccount.Id, resource.Name!, resource.Address!, resource.DepartmentCount!.Value);
        db.Set<Building>().Add(building);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return StatusCode(StatusCodes.Status201Created, new
        {
            ownerAdminId = buildingAccount.ExternalId,
            buildingId = building.Id,
            name = building.Name,
            address = building.Address,
            departmentCount = building.DepartmentCount,
            role = TenantRoles.Owner,
            isPortfolioBuilding = true,
        });
    }

    private IActionResult TenantRequired() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "TENANT_REQUIRED",
            message = "El usuario autenticado no tiene acceso a ese condominio.",
        });
}
