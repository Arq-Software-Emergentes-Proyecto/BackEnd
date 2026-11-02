using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Team.Application.Internal.CommandServices;
using BuildingFex.Api.Team.Interfaces.Rest.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Team.Interfaces.Rest;

/// <summary>TasksController (Tabla 81): tareas de mantenimiento del condominio — solo admin.</summary>
[ApiController]
[Route("api/v1/tasks")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class TasksController(MaintenanceTaskCommandService taskService) : TeamControllerBase
{
    /// <summary>Filtros: staffId, status (PENDING|IN_PROGRESS|COMPLETED), incidentId, open=true.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? staffId,
        [FromQuery] string? status,
        [FromQuery] string? incidentId,
        [FromQuery] bool? open,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var tasks = await taskService.ListAsync(caller!.TenantExternalId, staffId, status, incidentId, open == true, ct);
        return Ok(tasks.Select(TeamResourceAssembler.ToResource));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var task = await taskService.FindAsync(caller!.TenantExternalId, id, ct);
        return task is null
            ? NotFound(new { code = "TASK_NOT_FOUND", message = "Tarea no encontrada." })
            : Ok(TeamResourceAssembler.ToResource(task));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMaintenanceTaskResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var result = await taskService.Handle(new CreateTaskCommand(
            caller!.TenantExternalId,
            caller.TenantId,
            resource.Id,
            resource.StaffId ?? string.Empty,
            resource.IncidentId,
            resource.Description ?? string.Empty,
            resource.ScheduledFor), ct);
        if (result.IsFailure) return TeamFailure(result.Error, result.Message);

        return StatusCode(StatusCodes.Status201Created, TeamResourceAssembler.ToResource(result.Value!));
    }

    [HttpPatch("{id}/start")]
    public async Task<IActionResult> Start(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var result = await taskService.StartAsync(caller!.TenantExternalId, id, ct);
        return result.IsFailure ? TeamFailure(result.Error, result.Message) : Ok(TeamResourceAssembler.ToResource(result.Value!));
    }

    [HttpPatch("{id}/complete")]
    public async Task<IActionResult> Complete(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var result = await taskService.CompleteAsync(caller!.TenantExternalId, id, ct);
        return result.IsFailure ? TeamFailure(result.Error, result.Message) : Ok(TeamResourceAssembler.ToResource(result.Value!));
    }

    [HttpPatch("{id}/reassign")]
    public async Task<IActionResult> Reassign(string id, [FromBody] ReassignMaintenanceTaskResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(resource.StaffId))
            return BadRequest(new { code = "TASK_FIELDS_REQUIRED", message = "staffId es obligatorio." });

        var result = await taskService.ReassignAsync(caller!.TenantExternalId, id, resource.StaffId, ct);
        return result.IsFailure ? TeamFailure(result.Error, result.Message) : Ok(TeamResourceAssembler.ToResource(result.Value!));
    }
}
