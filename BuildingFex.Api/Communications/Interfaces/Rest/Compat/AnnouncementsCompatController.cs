using BuildingFex.Api.Communications.Application.CommandServices;
using BuildingFex.Api.Communications.Application.QueryServices;
using BuildingFex.Api.Communications.Domain.Model;
using BuildingFex.Api.Communications.Domain.Model.Commands;
using BuildingFex.Api.Communications.Domain.Repositories;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Shared.Interfaces.Rest;
using BuildingFex.Api.Communications.Interfaces.Rest.Resources;
using BuildingFex.Api.Communications.Interfaces.Rest.Transform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Communications.Interfaces.Rest.Compat;

[ApiController]
[Route("announcements")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class AnnouncementsCompatController(
    IAnnouncementQueryService announcementQueryService,
    IAnnouncementCommandService announcementCommandService,
    IAnnouncementRepository announcementRepository) : TenantScopedControllerBase
{
    /// <summary>
    /// Residentes: SOLO comunicados vigentes (filtrado en BD, RF019/RNF011), ordenados por prioridad y fecha (RF020).
    /// Admin: todos (incluye vencidos, con status "expired") salvo <c>activeOnly=true</c>.
    /// <c>includeExpired=false</c> equivale a activeOnly para el admin; un residente nunca recibe vencidos.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? ownerAdminId,
        [FromQuery] bool? activeOnly,
        [FromQuery] bool? includeExpired,
        CancellationToken ct)
    {
        // Antes: sin ownerAdminId devolvía comunicados de TODOS los edificios.
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;

        var onlyActive = caller!.IsResident || activeOnly == true || includeExpired == false;
        var announcements = onlyActive
            ? await announcementQueryService.ListActiveByOwnerExternalIdAsync(caller.TenantExternalId, ct)
            : await announcementQueryService.ListByOwnerExternalIdAsync(caller.TenantExternalId, ct);

        var now = DateTimeOffset.UtcNow;
        return Ok(announcements.Select(a => AnnouncementResourceAssembler.ToResource(a, now)));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Create([FromBody] CreateAnnouncementCompatResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(resource.OwnerAdminId ?? resource.AuthorId, ct);
        if (error is not null) return error;

        var result = await announcementCommandService.Handle(
            new CreateAnnouncementCommand(
                resource.Id ?? string.Empty,
                caller!.TenantExternalId,
                resource.Title ?? string.Empty,
                resource.Body ?? string.Empty,
                resource.Priority ?? "normal",
                resource.Duration,
                resource.AuthorName,
                null,
                null),
            ct);

        if (result.IsFailure)
            return BadRequest(new { code = MapErrorCode(result.Error), message = result.Message });

        return StatusCode(StatusCodes.Status201Created, AnnouncementResourceAssembler.ToResource(result.Value!));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpdateAnnouncementCompatResource resource,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var existing = await announcementRepository.FindByExternalIdAsync(id, ct);
        if (existing is null)
            return NotFound(new { code = "ANNOUNCEMENT_NOT_FOUND", message = "Comunicado no encontrado." });
        if (!BelongsToTenant(caller!, existing.OwnerAdminId))
            return ForbiddenTenant(caller!, "announcement", id);

        var result = await announcementCommandService.Handle(
            new UpdateAnnouncementCommand(
                id,
                resource.Title ?? string.Empty,
                resource.Body ?? string.Empty,
                resource.Priority ?? "normal",
                resource.Duration),
            ct);

        if (result.IsFailure)
            return result.Error switch
            {
                AnnouncementError.AnnouncementNotFound => NotFound(new { code = "ANNOUNCEMENT_NOT_FOUND", message = result.Message }),
                _ => BadRequest(new { code = MapErrorCode(result.Error), message = result.Message }),
            };

        return Ok(AnnouncementResourceAssembler.ToResource(result.Value!));
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var existing = await announcementRepository.FindByExternalIdAsync(id, ct);
        if (existing is null)
            return NotFound(new { code = "ANNOUNCEMENT_NOT_FOUND", message = "Comunicado no encontrado." });
        if (!BelongsToTenant(caller!, existing.OwnerAdminId))
            return ForbiddenTenant(caller!, "announcement", id);

        var result = await announcementCommandService.Handle(new DeleteAnnouncementCommand(id), ct);
        if (result.IsFailure)
            return NotFound(new { code = "ANNOUNCEMENT_NOT_FOUND", message = result.Message });

        return NoContent();
    }

    private static string MapErrorCode(Enum? error) =>
        error switch
        {
            AnnouncementError.AnnouncementNotFound => "ANNOUNCEMENT_NOT_FOUND",
            AnnouncementError.OwnerAdminRequired => "OWNER_ADMIN_REQUIRED",
            AnnouncementError.TitleRequired => "TITLE_REQUIRED",
            AnnouncementError.InvalidDuration => "INVALID_DURATION",
            _ => "ANNOUNCEMENT_ERROR",
        };
}
