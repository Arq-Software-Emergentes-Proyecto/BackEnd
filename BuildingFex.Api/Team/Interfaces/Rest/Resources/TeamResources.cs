using BuildingFex.Api.Team.Domain.Model.Aggregates;
using BuildingFex.Api.Team.Domain.Model.Entities;

namespace BuildingFex.Api.Team.Interfaces.Rest.Resources;

public class CreateTeamWorkerResource
{
    public string? Id { get; set; }
    public string? OwnerAdminId { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Dni { get; set; }
    public decimal Salary { get; set; }
    public string? PhotoUrl { get; set; }
    /// <summary>CONCIERGE | MAINTENANCE | CLEANING | SECURITY (opcional; por defecto MAINTENANCE).</summary>
    public string? Position { get; set; }
}

public class PatchTeamWorkerResource
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Dni { get; set; }
    public decimal? Salary { get; set; }
    public string? PhotoUrl { get; set; }
    public string? Position { get; set; }
}

public class DeactivateTeamWorkerResource
{
    /// <summary>Trabajador que recibe las tareas abiertas del desactivado (opcional).</summary>
    public string? ReassignToId { get; set; }
}

public class CreateMaintenanceTaskResource
{
    public string? Id { get; set; }
    public string? StaffId { get; set; }
    public string? IncidentId { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }
}

public class ReassignMaintenanceTaskResource
{
    public string? StaffId { get; set; }
}

public class CreateShiftResource
{
    public string? Id { get; set; }
    public string? StaffId { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public string? Notes { get; set; }
}

public class UpdateShiftResource
{
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public string? Notes { get; set; }
}

public static class TeamResourceAssembler
{
    public static object ToResource(TeamWorker w, int? openTasks = null) => new
    {
        id = w.ExternalId,
        ownerAdminId = w.OwnerAdminId,
        name = w.Name,
        phone = w.Phone,
        dni = w.Dni,
        salary = w.Salary,
        photoUrl = w.PhotoUrl,
        position = w.Position,
        status = w.Status,
        isActive = w.IsActive,
        deactivatedAt = w.DeactivatedAt?.ToString("o"),
        openTasks,
    };

    public static object ToResource(MaintenanceTask t) => new
    {
        id = t.ExternalId,
        ownerAdminId = t.OwnerAdminId,
        staffId = t.StaffId,
        incidentId = t.IncidentId,
        description = t.Description,
        scheduledFor = t.ScheduledFor?.ToString("o"),
        status = t.Status,
        startedAt = t.StartedAt?.ToString("o"),
        completedAt = t.CompletedAt?.ToString("o"),
        createdAt = t.CreatedAt.ToString("o"),
        updatedAt = t.UpdatedAt?.ToString("o"),
    };

    public static object ToResource(Shift s) => new
    {
        id = s.ExternalId,
        ownerAdminId = s.OwnerAdminId,
        staffId = s.StaffId,
        start = s.StartsAt.ToString("o"),
        end = s.EndsAt.ToString("o"),
        notes = s.Notes,
        createdAt = s.CreatedAt.ToString("o"),
    };
}
