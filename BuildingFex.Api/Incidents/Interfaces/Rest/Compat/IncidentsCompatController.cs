using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Incidents.Application.CommandServices;
using BuildingFex.Api.Incidents.Application.Internal.CommandServices;
using BuildingFex.Api.Incidents.Application.QueryServices;
using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model.Commands;
using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;
using BuildingFex.Api.Incidents.Domain.Repositories;
using BuildingFex.Api.Incidents.Interfaces.Rest.Resources;
using BuildingFex.Api.Incidents.Interfaces.Rest.Transform;
using BuildingFex.Api.ResidentCare.Interfaces.Hubs;
using BuildingFex.Api.Shared.Application.Model;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace BuildingFex.Api.Incidents.Interfaces.Rest.Compat;

/// <summary>
/// Incidencias (compat json-server + endpoints del diseño táctico: PATCH assign / resolve).
/// Tiempo real: reutiliza SupportHub (/hubs/support) — "incidentCreated" al grupo del admin
/// y "incidentUpdated" al admin y al residente que reportó.
/// </summary>
[ApiController]
[Route("incidents")]
[Route("api/v1/incidents")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class IncidentsCompatController(
    IIncidentQueryService incidentQueryService,
    IIncidentCommandService incidentCommandService,
    IIncidentRepository incidentRepository,
    IServiceProviderRepository serviceProviderRepository,
    IHubContext<SupportHub> hub,
    ILogger<IncidentsCompatController> logger) : TenantScopedControllerBase
{
    /// <summary>
    /// Admin: incidencias del condominio (filtro opcional residentId/status).
    /// Residente: solo las que él reportó (filtrado en BD; residentId ajeno → 403).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? ownerAdminId,
        [FromQuery] string? residentId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;

        var scopeError = ResolveResidentScope(caller!, residentId, out var effectiveResidentId);
        if (scopeError is not null) return scopeError;

        var incidents = await incidentQueryService.ListAsync(
            caller!.TenantExternalId, effectiveResidentId, status, ct);
        var providers = await ProvidersByIdAsync(caller, ct);

        return Ok(incidents.Select(i => IncidentResourceAssembler.ToResource(i, ProviderFor(i, providers), false)));
    }

    /// <summary>Detalle con historial (timeline). Residente: solo sus incidencias.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var (incident, accessError) = await FindAccessibleAsync(caller!, id, ct);
        if (accessError is not null) return accessError;

        return Ok(await ToResourceAsync(caller!, incident!, includeEvents: true, ct));
    }

    /// <summary>
    /// Residentes reportan incidencias propias (residentId/nombre/estado los fija el servidor).
    /// Admins pueden registrar incidencias de cualquier residente de su condominio.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateIncidentCompatResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(resource.OwnerAdminId, ct);
        if (error is not null) return error;

        var residentId = resource.ResidentId;
        var residentName = resource.ResidentName;
        var status = resource.Status ?? IncidentStatuses.Open;
        var provider = resource.Provider;
        var providerId = resource.ProviderId;

        if (caller!.IsResident)
        {
            if (!string.IsNullOrWhiteSpace(residentId)
                && !string.Equals(residentId.Trim(), caller.UserExternalId, StringComparison.Ordinal))
                return ForbiddenTenant(caller, "residentId", residentId);

            residentId = caller.UserExternalId;
            residentName = string.IsNullOrWhiteSpace(caller.User.Name) ? residentName : caller.User.Name;
            status = IncidentStatuses.Open;
            provider = null;
            providerId = null;
        }
        else if (IncidentStatuses.Normalize(status) == IncidentStatuses.Resolved)
        {
            // Una incidencia no nace resuelta sin detalle de cierre: se usa PATCH /resolve.
            status = IncidentStatuses.Open;
        }

        var result = await incidentCommandService.Handle(
            new CreateIncidentCommand(
                resource.Id ?? string.Empty,
                caller.TenantExternalId,
                resource.Description ?? string.Empty,
                status,
                residentId,
                residentName,
                provider,
                resource.CreatedAt,
                resource.Title ?? resource.Subject,
                resource.Category,
                resource.Priority,
                providerId,
                Actor(caller)),
            ct);

        if (result.IsFailure)
            return Failure(result);

        var created = result.Value!;
        var body = await ToResourceAsync(caller, created, includeEvents: false, ct);
        await NotifyAsync("incidentCreated", caller.TenantExternalId, created, body);
        return StatusCode(StatusCodes.Status201Created, body);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpdateIncidentCompatResource resource,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(resource.OwnerAdminId, ct);
        if (error is not null) return error;

        var (_, accessError) = await FindAccessibleAsync(caller!, id, ct);
        if (accessError is not null) return accessError;

        var result = await incidentCommandService.Handle(
            new UpdateIncidentCommand(
                id,
                resource.Description ?? string.Empty,
                resource.Status ?? IncidentStatuses.Open,
                resource.Provider,
                resource.ResidentId,
                resource.ResidentName,
                resource.Title,
                resource.Category,
                resource.Priority,
                resource.ProviderId,
                resource.ResolutionComment,
                Actor(caller!)),
            ct);

        return await UpdatedResponseAsync(caller!, result, ct);
    }

    /// <summary>assign(): asigna (providerId) o desasigna (providerId vacío) el proveedor. Abierta → en proceso.</summary>
    [HttpPatch("{id}/assign")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Assign(string id, [FromBody] AssignIncidentResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var (_, accessError) = await FindAccessibleAsync(caller!, id, ct);
        if (accessError is not null) return accessError;

        var result = await incidentCommandService.Handle(
            new AssignIncidentCommand(id, resource.ProviderId, resource.Provider, resource.Status, Actor(caller!)),
            ct);

        return await UpdatedResponseAsync(caller!, result, ct);
    }

    /// <summary>resolve(): exige comentario de resolución (400 RESOLUTION_COMMENT_REQUIRED).</summary>
    [HttpPatch("{id}/resolve")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Resolve(string id, [FromBody] ResolveIncidentResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var (_, accessError) = await FindAccessibleAsync(caller!, id, ct);
        if (accessError is not null) return accessError;

        var result = await incidentCommandService.Handle(
            new ResolveIncidentCommand(id, resource.ResolutionComment ?? resource.Comment, Actor(caller!)),
            ct);

        return await UpdatedResponseAsync(caller!, result, ct);
    }

    /// <summary>Comentario de seguimiento: admin del condominio o el residente que la reportó.</summary>
    [HttpPost("{id}/comments")]
    public async Task<IActionResult> AddComment(string id, [FromBody] AddIncidentCommentResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var (_, accessError) = await FindAccessibleAsync(caller!, id, ct);
        if (accessError is not null) return accessError;

        var result = await incidentCommandService.Handle(
            new AddIncidentCommentCommand(id, resource.Comment, Actor(caller!)),
            ct);

        return await UpdatedResponseAsync(caller!, result, ct);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var (_, accessError) = await FindAccessibleAsync(caller!, id, ct);
        if (accessError is not null) return accessError;

        var result = await incidentCommandService.Handle(new DeleteIncidentCommand(id), ct);
        if (result.IsFailure)
            return NotFound(new { code = "INCIDENT_NOT_FOUND", message = result.Message });

        return NoContent();
    }

    private async Task<(Incident? Incident, IActionResult? Error)> FindAccessibleAsync(
        CallerContext caller,
        string id,
        CancellationToken ct)
    {
        var existing = await incidentRepository.FindByExternalIdAsync(id, ct);
        if (existing is null)
            return (null, NotFound(new { code = "INCIDENT_NOT_FOUND", message = "Incidencia no encontrada." }));
        if (!BelongsToTenant(caller, existing.OwnerAdminId))
            return (null, ForbiddenTenant(caller, "incident", id));
        if (caller.IsResident
            && !string.Equals(existing.ResidentExternalId, caller.UserExternalId, StringComparison.Ordinal))
            return (null, ForbiddenTenant(caller, "incident", id));
        return (existing, null);
    }

    private async Task<IActionResult> UpdatedResponseAsync(
        CallerContext caller,
        Result<Incident> result,
        CancellationToken ct)
    {
        if (result.IsFailure)
            return Failure(result);

        var body = await ToResourceAsync(caller, result.Value!, includeEvents: true, ct);
        await NotifyAsync("incidentUpdated", caller.TenantExternalId, result.Value!, body);
        return Ok(body);
    }

    private IActionResult Failure(Result<Incident> result) =>
        result.Error switch
        {
            IncidentError.IncidentNotFound => NotFound(new { code = "INCIDENT_NOT_FOUND", message = result.Message }),
            _ => BadRequest(new { code = MapErrorCode(result.Error), message = result.Message }),
        };

    private async Task<IncidentResource> ToResourceAsync(
        CallerContext caller,
        Incident incident,
        bool includeEvents,
        CancellationToken ct)
    {
        var providers = await ProvidersByIdAsync(caller, ct);
        return IncidentResourceAssembler.ToResource(
            incident, ProviderFor(incident, providers), includeEvents);
    }

    private async Task<Dictionary<string, BuildingServiceProvider>> ProvidersByIdAsync(CallerContext caller, CancellationToken ct)
    {
        var list = await serviceProviderRepository.ListByOwnerAsync(caller.TenantExternalId, ct);
        var map = new Dictionary<string, BuildingServiceProvider>(StringComparer.Ordinal);
        foreach (var p in list) map.TryAdd(p.ExternalId, p);
        return map;
    }

    /// <summary>assigneeId; incidencias legacy (solo nombre) se casan por nombre.</summary>
    private static BuildingServiceProvider? ProviderFor(Incident incident, Dictionary<string, BuildingServiceProvider> providers)
    {
        if (!string.IsNullOrWhiteSpace(incident.AssigneeId) && providers.TryGetValue(incident.AssigneeId, out var byId))
            return byId;
        if (!string.IsNullOrWhiteSpace(incident.AssigneeId) || string.IsNullOrWhiteSpace(incident.Provider))
            return null;
        var name = incident.Provider.Trim();
        return providers.Values.FirstOrDefault(p => string.Equals(p.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    private static IncidentActor Actor(CallerContext caller) =>
        new(caller.UserExternalId, caller.User.Name, caller.User.Role);

    /// <summary>Notificación en tiempo real; un fallo de SignalR no debe romper la operación.</summary>
    private async Task NotifyAsync(string eventName, string tenantExternalId, Incident incident, IncidentResource body)
    {
        try
        {
            await hub.Clients.Group(SupportHub.OwnerGroup(tenantExternalId)).SendAsync(eventName, body);
            if (eventName != "incidentCreated" && !string.IsNullOrWhiteSpace(incident.ResidentExternalId))
                await hub.Clients.Group(SupportHub.ResidentGroup(tenantExternalId, incident.ResidentExternalId))
                    .SendAsync(eventName, body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Incident realtime notification {Event} failed for {Incident}", eventName, incident.ExternalId);
        }
    }

    private static string MapErrorCode(Enum? error) =>
        error switch
        {
            IncidentError.IncidentNotFound => "INCIDENT_NOT_FOUND",
            IncidentError.OwnerAdminRequired => "OWNER_ADMIN_REQUIRED",
            IncidentError.DescriptionRequired => "DESCRIPTION_REQUIRED",
            IncidentError.InvalidStatus => "INVALID_STATUS",
            IncidentError.ResidentRequired => "RESIDENT_REQUIRED",
            IncidentError.ProviderRequired => "PROVIDER_REQUIRED",
            IncidentError.TitleRequired => "TITLE_REQUIRED",
            IncidentError.ResolutionCommentRequired => "RESOLUTION_COMMENT_REQUIRED",
            IncidentError.CommentRequired => "COMMENT_REQUIRED",
            IncidentError.ProviderNotFound => "PROVIDER_NOT_FOUND",
            _ => "INCIDENT_ERROR",
        };
}
