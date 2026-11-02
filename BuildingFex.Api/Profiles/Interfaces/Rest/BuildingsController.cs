using BuildingFex.Api.Billing.Application.Internal;
using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Iam.Application.QueryServices;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Iam.Interfaces.Rest.Transform;
using BuildingFex.Api.Profiles.Domain.Model.Aggregates;
using BuildingFex.Api.Profiles.Domain.Repositories;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Profiles.Interfaces.Rest;

/// <summary>
/// Registro de condominios (RF015 / US015 / TS001) y padrón por edificio (TS002).
/// Tenant = admin del token; un edificio de otro condominio responde 403 (auditado).
/// </summary>
[ApiController]
[Route("api/v1/buildings")]
[Authorize]
public class BuildingsController(
    IBuildingRepository buildingRepository,
    IUserQueryService userQueryService,
    ISubscriptionService subscriptionService,
    IUnitOfWork unitOfWork) : TenantScopedControllerBase
{
    /// <summary>TS001: 201 + id; 400 ValidationProblem con el detalle por campo.</summary>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Create([FromBody] UpsertBuildingResource? resource, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return tenant.Error;
        var caller = tenant.Caller!;
        if (!caller.IsAdmin) return AdminRequired();

        var invalid = ValidateOrNull(resource);
        if (invalid is not null) return invalid;

        var admin = caller.TenantAdmin;
        var existing = await buildingRepository.ListByOwnerAdminIdAsync(admin.Id, ct);
        // Un edificio por condominio: cada edificio adicional del plan Portafolio es un condominio aparte
        // (POST /api/v1/portfolio/buildings), para que sus datos no se mezclen.
        if (existing.Count > 0)
        {
            var portfolio = SubscriptionPlans.EffectivePlanId(admin.SubscriptionPlanId, admin.SubscriptionPaidUntil, DateTimeOffset.UtcNow)
                            == SubscriptionPlanIds.Portafolio;
            return Conflict(new
            {
                code = "BUILDING_ALREADY_REGISTERED",
                message = portfolio
                    ? "Este condominio ya tiene su edificio. Agrega los demás desde Mis edificios."
                    : "Tu condominio ya tiene un edificio registrado. Para varios edificios contrata el plan Portafolio.",
                id = existing[0].Id,
            });
        }

        var building = Building.Register(admin.Id, resource!.Name!, resource.Address!, resource.DepartmentCount!.Value);
        await buildingRepository.AddAsync(building, ct);
        await unitOfWork.CompleteAsync(ct);
        await subscriptionService.LinkBuildingAsync(admin, building.Id, ct);

        return CreatedAtAction(nameof(GetById), new { id = building.Id }, ToResource(building, caller.TenantExternalId));
    }

    /// <summary>Edificios del condominio del usuario autenticado (admin o residente).</summary>
    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return tenant.Error;
        var caller = tenant.Caller!;

        var buildings = await buildingRepository.ListByOwnerAdminIdAsync(caller.TenantId, ct);
        return Ok(buildings.Select(b => ToResource(b, caller.TenantExternalId)));
    }

    [HttpGet("{id:int}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var (building, error) = await LoadOwnedAsync(id, ct);
        if (building is null) return error!;
        var caller = (await ResolveTenantAsync(null, ct)).Caller!;
        return Ok(ToResource(building, caller.TenantExternalId));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Update(int id, [FromBody] UpsertBuildingResource? resource, CancellationToken ct)
    {
        var (building, error) = await LoadOwnedAsync(id, ct);
        if (building is null) return error!;
        var caller = (await ResolveTenantAsync(null, ct)).Caller!;
        if (!caller.IsAdmin) return AdminRequired();

        var invalid = ValidateOrNull(resource);
        if (invalid is not null) return invalid;

        building.Update(resource!.Name!, resource.Address!, resource.DepartmentCount!.Value);
        buildingRepository.Update(building);
        await unitOfWork.CompleteAsync(ct);
        return Ok(ToResource(building, caller.TenantExternalId));
    }

    /// <summary>
    /// TS002: residentes del edificio. El padrón todavía se agrupa por condominio (tenant), no por edificio:
    /// fuera de Portafolio hay un único edificio, así que la lista es exacta.
    /// </summary>
    [HttpGet("{id:int}/residents")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetResidents(int id, CancellationToken ct)
    {
        var (building, error) = await LoadOwnedAsync(id, ct);
        if (building is null) return error!;
        var caller = (await ResolveTenantAsync(null, ct)).Caller!;
        if (!caller.IsAdmin) return AdminRequired();

        var residents = await userQueryService.SearchAsync(
            email: null,
            role: "resident",
            code: null,
            ownerAdminExternalId: caller.TenantExternalId,
            cancellationToken: ct);
        return Ok(residents.Select(UserResourceAssembler.ToResource));
    }

    private async Task<(Building? Building, IActionResult? Error)> LoadOwnedAsync(int id, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return (null, tenant.Error);
        var caller = tenant.Caller!;

        var building = await buildingRepository.FindByIdAsync(id, ct);
        if (building is null)
            return (null, NotFound(new { code = "BUILDING_NOT_FOUND", message = "Edificio no encontrado." }));
        if (!BelongsToTenant(caller, building.OwnerAdminId))
            return (null, ForbiddenTenant(caller, "buildingId", id.ToString()));
        return (building, null);
    }

    private IActionResult? ValidateOrNull(UpsertBuildingResource? resource)
    {
        var errors = Building.Validate(resource?.Name, resource?.Address, resource?.DepartmentCount);
        if (errors.Count == 0) return null;

        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Datos del edificio inválidos.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        };
        problem.Extensions["code"] = "BUILDING_VALIDATION_FAILED";
        return BadRequest(problem);
    }

    private IActionResult AdminRequired() =>
        StatusCode(StatusCodes.Status403Forbidden, new { code = "ADMIN_REQUIRED", message = "Solo administradores." });

    private static BuildingResource ToResource(Building building, string ownerAdminExternalId) =>
        new(
            building.Id,
            building.Name,
            building.Address,
            building.DepartmentCount,
            ownerAdminExternalId,
            PlanCatalog.SuggestForDepartments(building.DepartmentCount),
            building.CreatedAt?.ToString("o"),
            building.UpdatedAt?.ToString("o"));
}

/// <summary>Campos opcionales a propósito: la validación devuelve el detalle por campo (TS001).</summary>
public class UpsertBuildingResource
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public int? DepartmentCount { get; set; }
}

public record BuildingResource(
    int Id,
    string Name,
    string Address,
    int DepartmentCount,
    string OwnerAdminId,
    string SuggestedPlanId,
    string? CreatedAt,
    string? UpdatedAt);
