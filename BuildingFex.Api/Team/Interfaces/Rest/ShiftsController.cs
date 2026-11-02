using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Team.Application.Internal.CommandServices;
using BuildingFex.Api.Team.Interfaces.Rest.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Team.Interfaces.Rest;

/// <summary>Turnos del personal — solo admin. Un turno que se cruza con otro del mismo trabajador → 409.</summary>
[ApiController]
[Route("api/v1/shifts")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class ShiftsController(ShiftCommandService shiftService) : TeamControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? staffId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var shifts = await shiftService.ListAsync(caller!.TenantExternalId, staffId, from, to, ct);
        return Ok(shifts.Select(TeamResourceAssembler.ToResource));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var shift = await shiftService.FindAsync(caller!.TenantExternalId, id, ct);
        return shift is null
            ? NotFound(new { code = "SHIFT_NOT_FOUND", message = "Turno no encontrado." })
            : Ok(TeamResourceAssembler.ToResource(shift));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateShiftResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(resource.StaffId) || resource.Start is null || resource.End is null)
            return BadRequest(new { code = "SHIFT_FIELDS_REQUIRED", message = "staffId, start y end son obligatorios." });

        var result = await shiftService.CreateAsync(
            caller!.TenantExternalId, resource.Id, resource.StaffId, resource.Start.Value, resource.End.Value, resource.Notes, ct);
        if (result.IsFailure) return TeamFailure(result.Error, result.Message);

        return StatusCode(StatusCodes.Status201Created, TeamResourceAssembler.ToResource(result.Value!));
    }

    [HttpPut("{id}")]
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateShiftResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var result = await shiftService.RescheduleAsync(
            caller!.TenantExternalId, id, resource.Start, resource.End, resource.Notes, ct);
        return result.IsFailure ? TeamFailure(result.Error, result.Message) : Ok(TeamResourceAssembler.ToResource(result.Value!));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        return await shiftService.DeleteAsync(caller!.TenantExternalId, id, ct)
            ? NoContent()
            : NotFound(new { code = "SHIFT_NOT_FOUND", message = "Turno no encontrado." });
    }
}
