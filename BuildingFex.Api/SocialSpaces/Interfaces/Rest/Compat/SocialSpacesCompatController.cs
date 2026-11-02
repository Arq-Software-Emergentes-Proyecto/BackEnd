using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Shared.Interfaces.Rest;
using BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;
using BuildingFex.Api.SocialSpaces.Domain.Repositories;
using BuildingFex.Api.SocialSpaces.Interfaces.Rest.Transform;
using BuildingFex.Api.Shared.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BuildingFex.Api.SocialSpaces.Interfaces.Rest.Compat;

[ApiController]
[Route("socialSpaces")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class SocialSpacesCompatController(
    ISocialSpaceRepository socialSpaceRepository,
    IUnitOfWork unitOfWork) : TenantScopedControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? ownerAdminId, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;

        var spaces = await socialSpaceRepository.ListByOwnerExternalIdAsync(caller!.TenantExternalId, ct);
        return Ok(spaces.Select(SocialSpacesCompatSerializer.SpaceToJson));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var space = await socialSpaceRepository.FindByExternalIdAsync(id, ct);
        if (space is null)
            return NotFound();
        if (!BelongsToTenant(caller!, space.OwnerAdminId))
            return ForbiddenTenant(caller!, "socialSpace", id);

        return Ok(SocialSpacesCompatSerializer.SpaceToJson(space));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(BodyString(body, "ownerAdminId"), ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        var name = body.TryGetProperty("name", out var nameProp) ? nameProp.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { code = "SPACE_NAME_REQUIRED", message = "El nombre es obligatorio." });

        var externalId = body.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? $"space-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : $"space-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        int? capacity = null;
        if (body.TryGetProperty("capacity", out var capProp) && capProp.ValueKind == JsonValueKind.Number)
            capacity = capProp.GetInt32();

        var space = SocialSpace.Create(
            externalId,
            owner.Id,
            name,
            body.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? string.Empty : string.Empty,
            capacity,
            body.TryGetProperty("imageUrl", out var imgProp) ? imgProp.GetString() ?? string.Empty : string.Empty);

        ApplyOperationalFields(space, body);

        await socialSpaceRepository.AddAsync(space, ct);
        await unitOfWork.CompleteAsync(ct);

        var created = await socialSpaceRepository.FindByExternalIdAsync(externalId, ct);
        return StatusCode(StatusCodes.Status201Created, SocialSpacesCompatSerializer.SpaceToJson(created!));
    }

    [HttpPatch("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Patch(string id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var space = await socialSpaceRepository.FindByExternalIdAsync(id, ct);
        if (space is null)
            return NotFound(new { code = "SPACE_NOT_FOUND", message = "Espacio no encontrado." });
        if (!BelongsToTenant(caller!, space.OwnerAdminId))
            return ForbiddenTenant(caller!, "socialSpace", id);

        var name = body.TryGetProperty("name", out var nameProp) ? nameProp.GetString()?.Trim() : space.Name;
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { code = "SPACE_NAME_REQUIRED", message = "El nombre es obligatorio." });

        int? capacity = space.Capacity;
        if (body.TryGetProperty("capacity", out var capProp))
            capacity = capProp.ValueKind == JsonValueKind.Null ? null : capProp.GetInt32();

        string? imageUrl = body.TryGetProperty("imageUrl", out var imgProp)
            ? imgProp.GetString() ?? string.Empty
            : null;

        space.Patch(
            name,
            body.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? string.Empty : space.Description,
            capacity,
            imageUrl);
        ApplyOperationalFields(space, body);

        socialSpaceRepository.Update(space);
        await unitOfWork.CompleteAsync(ct);

        var updated = await socialSpaceRepository.FindByExternalIdAsync(id, ct);
        return Ok(SocialSpacesCompatSerializer.SpaceToJson(updated!));
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var space = await socialSpaceRepository.FindByExternalIdAsync(id, ct);
        if (space is null)
            return NotFound(new { code = "SPACE_NOT_FOUND", message = "Espacio no encontrado." });
        if (!BelongsToTenant(caller!, space.OwnerAdminId))
            return ForbiddenTenant(caller!, "socialSpace", id);

        socialSpaceRepository.Remove(space);
        await unitOfWork.CompleteAsync(ct);
        return NoContent();
    }

    /// <summary>Reglamento, estado (AVAILABLE / MAINTENANCE) y cupo diario: solo se cambian si vienen en el body.</summary>
    private static void ApplyOperationalFields(SocialSpace space, JsonElement body)
    {
        if (body.TryGetProperty("rules", out var rulesProp))
            space.SetRules(rulesProp.ValueKind == JsonValueKind.String ? rulesProp.GetString() : null);

        if (body.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
            space.SetStatus(statusProp.GetString());

        if (body.TryGetProperty("maxReservationsPerDay", out var maxProp))
            space.SetMaxReservationsPerDay(
                maxProp.ValueKind == JsonValueKind.Number && maxProp.TryGetInt32(out var max) ? max : null);
    }
}
