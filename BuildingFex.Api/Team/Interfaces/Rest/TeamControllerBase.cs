using BuildingFex.Api.Shared.Interfaces.Rest;
using BuildingFex.Api.Team.Domain.Model;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Team.Interfaces.Rest;

/// <summary>Traduce los errores de dominio de Team a HTTP (validación 400, no encontrado 404, regla 409).</summary>
public abstract class TeamControllerBase : TenantScopedControllerBase
{
    protected IActionResult TeamFailure(Enum? error, string message)
    {
        var (status, code) = error switch
        {
            TeamError.WorkerFieldsRequired => (StatusCodes.Status400BadRequest, "TEAM_WORKER_FIELDS_REQUIRED"),
            TeamError.InvalidPosition => (StatusCodes.Status400BadRequest, "INVALID_POSITION"),
            TeamError.TaskFieldsRequired => (StatusCodes.Status400BadRequest, "TASK_FIELDS_REQUIRED"),
            TeamError.ShiftInvalidRange => (StatusCodes.Status400BadRequest, "SHIFT_INVALID_RANGE"),
            TeamError.WorkerNotFound => (StatusCodes.Status404NotFound, "TEAM_WORKER_NOT_FOUND"),
            TeamError.TaskNotFound => (StatusCodes.Status404NotFound, "TASK_NOT_FOUND"),
            TeamError.ShiftNotFound => (StatusCodes.Status404NotFound, "SHIFT_NOT_FOUND"),
            TeamError.IncidentNotFound => (StatusCodes.Status404NotFound, "INCIDENT_NOT_FOUND"),
            TeamError.WorkerInactive => (StatusCodes.Status409Conflict, "WORKER_INACTIVE"),
            TeamError.WorkloadExceeded => (StatusCodes.Status409Conflict, "WORKLOAD_EXCEEDED"),
            TeamError.InvalidTaskTransition => (StatusCodes.Status409Conflict, "INVALID_TASK_TRANSITION"),
            TeamError.ShiftOverlap => (StatusCodes.Status409Conflict, "SHIFT_OVERLAP"),
            _ => (StatusCodes.Status400BadRequest, "TEAM_ERROR"),
        };
        return StatusCode(status, new { code, message });
    }
}
