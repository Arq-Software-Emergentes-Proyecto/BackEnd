using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Team.Application.Internal.CommandServices;
using BuildingFex.Api.Team.Domain.Repositories;
using BuildingFex.Api.Team.Interfaces.Rest.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Team.Interfaces.Rest.Compat;

/// <summary>
/// StaffController (Tabla 81). Ruta histórica /teamWorkers (frontend) y alias /api/v1/staff.
/// </summary>
[ApiController]
[Route("teamWorkers")]
[Route("api/v1/staff")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class TeamWorkersCompatController(
    TeamWorkerCommandService staffService,
    ITeamWorkerRepository workers) : TeamControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? ownerAdminId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;

        var workload = await staffService.GetWorkloadAsync(caller!.TenantExternalId, status, ct);
        return Ok(workload.Select(x => TeamResourceAssembler.ToResource(x.Worker, x.OpenTasks)));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateTeamWorkerResource resource,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(resource.OwnerAdminId, ct);
        if (error is not null) return error;

        var result = await staffService.Handle(new RegisterStaffCommand(
            caller!.TenantExternalId,
            resource.Id,
            resource.Name ?? string.Empty,
            resource.Phone ?? string.Empty,
            resource.Dni ?? string.Empty,
            resource.Salary,
            resource.PhotoUrl ?? string.Empty,
            resource.Position), ct);
        if (result.IsFailure) return TeamFailure(result.Error, result.Message);

        return StatusCode(StatusCodes.Status201Created, TeamResourceAssembler.ToResource(result.Value!, 0));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] PatchTeamWorkerResource resource, CancellationToken ct)
    {
        var (caller, guard) = await GuardWorkerAsync(id, ct);
        if (guard is not null) return guard;

        var result = await staffService.Handle(new UpdateStaffCommand(
            caller!.TenantExternalId, id, resource.Name, resource.Phone, resource.Dni,
            resource.Salary, resource.PhotoUrl, resource.Position), ct);
        if (result.IsFailure) return TeamFailure(result.Error, result.Message);

        var open = await staffService.CountOpenTasksAsync(caller.TenantExternalId, result.Value!.ExternalId, ct);
        return Ok(TeamResourceAssembler.ToResource(result.Value!, open));
    }

    /// <summary>
    /// DeactivateStaff: el trabajador queda INACTIVE (no recibe tareas ni turnos). Con reassignToId
    /// (body o query) sus tareas abiertas pasan a ese trabajador, aplicando WorkloadPolicy.
    /// </summary>
    [HttpPatch("{id}/deactivate")]
    public async Task<IActionResult> Deactivate(
        string id,
        [FromBody] DeactivateTeamWorkerResource? resource,
        [FromQuery] string? reassignToId,
        CancellationToken ct)
    {
        var (caller, guard) = await GuardWorkerAsync(id, ct);
        if (guard is not null) return guard;

        var result = await staffService.DeactivateAsync(
            caller!.TenantExternalId, id, resource?.ReassignToId ?? reassignToId, ct);
        if (result.IsFailure) return TeamFailure(result.Error, result.Message);

        var value = result.Value!;
        return Ok(new
        {
            worker = TeamResourceAssembler.ToResource(value.Worker, value.OpenTasksLeft),
            reassignedTasks = value.ReassignedTasks,
            openTasksLeft = value.OpenTasksLeft,
        });
    }

    [HttpPatch("{id}/reactivate")]
    public async Task<IActionResult> Reactivate(string id, CancellationToken ct)
    {
        var (caller, guard) = await GuardWorkerAsync(id, ct);
        if (guard is not null) return guard;

        var result = await staffService.ReactivateAsync(caller!.TenantExternalId, id, ct);
        if (result.IsFailure) return TeamFailure(result.Error, result.Message);

        var open = await staffService.CountOpenTasksAsync(caller.TenantExternalId, result.Value!.ExternalId, ct);
        return Ok(TeamResourceAssembler.ToResource(result.Value!, open));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (_, guard) = await GuardWorkerAsync(id, ct);
        if (guard is not null) return guard;

        var worker = await workers.FindByIdentifierAsync(id, ct);
        if (worker is null) return NotFound();
        await staffService.DeleteAsync(worker, ct);
        return NoContent();
    }

    /// <summary>404 si no existe; 403 auditado si pertenece a otro condominio.</summary>
    private async Task<(Iam.Application.Internal.Tenancy.CallerContext? Caller, IActionResult? Error)> GuardWorkerAsync(
        string id,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return (null, error);

        var worker = await workers.FindByIdentifierAsync(id, ct);
        if (worker is null) return (caller, NotFound());
        if (!BelongsToTenant(caller!, worker.OwnerAdminId))
            return (caller, ForbiddenTenant(caller!, "teamWorker", id));
        return (caller, null);
    }
}
