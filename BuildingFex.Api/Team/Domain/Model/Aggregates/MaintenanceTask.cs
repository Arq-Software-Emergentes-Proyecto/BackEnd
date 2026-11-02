using BuildingFex.Api.Team.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Team.Domain.Model.Aggregates;

/// <summary>
/// MaintenanceTask (Tabla 80): tarea de mantenimiento, propia o generada por una incidencia (incidentId).
/// Ciclo: PENDING → IN_PROGRESS → COMPLETED. Solo una tarea abierta puede reasignarse.
/// </summary>
public class MaintenanceTask
{
    public const int DescriptionMaxLength = 2000;

    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string OwnerAdminId { get; private set; } = string.Empty;

    /// <summary>team_workers.external_id del trabajador asignado.</summary>
    public string StaffId { get; private set; } = string.Empty;

    /// <summary>incidents.external_id de origen (opcional).</summary>
    public string? IncidentId { get; private set; }

    public string Description { get; private set; } = string.Empty;
    public DateTimeOffset? ScheduledFor { get; private set; }
    public string Status { get; private set; } = MaintenanceTaskStatus.Pending;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool IsOpen => MaintenanceTaskStatus.IsOpen(Status);

    private MaintenanceTask() { }

    public static MaintenanceTask Create(
        string externalId,
        string ownerAdminId,
        string staffId,
        string description,
        DateTimeOffset? scheduledFor,
        string? incidentId)
    {
        var cleanDescription = (description ?? string.Empty).Trim();
        if (cleanDescription.Length == 0)
            throw new TeamDomainException(TeamError.TaskFieldsRequired, "La descripción es obligatoria.");
        if (cleanDescription.Length > DescriptionMaxLength)
            cleanDescription = cleanDescription[..DescriptionMaxLength];

        var task = new MaintenanceTask
        {
            ExternalId = externalId.Trim(),
            OwnerAdminId = ownerAdminId.Trim(),
            Description = cleanDescription,
            ScheduledFor = scheduledFor,
            IncidentId = string.IsNullOrWhiteSpace(incidentId) ? null : incidentId.Trim(),
            Status = MaintenanceTaskStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        task.AssignTo(staffId);
        return task;
    }

    public void AssignTo(string staffId)
    {
        var clean = (staffId ?? string.Empty).Trim();
        if (clean.Length == 0)
            throw new TeamDomainException(TeamError.TaskFieldsRequired, "staffId es obligatorio.");
        StaffId = clean;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Start()
    {
        if (Status != MaintenanceTaskStatus.Pending)
            throw new TeamDomainException(TeamError.InvalidTaskTransition, "Solo una tarea pendiente puede iniciarse.");
        Status = MaintenanceTaskStatus.InProgress;
        StartedAt = DateTimeOffset.UtcNow;
        UpdatedAt = StartedAt;
    }

    public void Complete()
    {
        if (!IsOpen)
            throw new TeamDomainException(TeamError.InvalidTaskTransition, "La tarea ya está completada.");
        StartedAt ??= DateTimeOffset.UtcNow;
        Status = MaintenanceTaskStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CompletedAt;
    }

    public void Reassign(string staffId)
    {
        if (!IsOpen)
            throw new TeamDomainException(TeamError.InvalidTaskTransition, "Una tarea completada no puede reasignarse.");
        AssignTo(staffId);
    }

    public void Reschedule(DateTimeOffset? scheduledFor, string? description)
    {
        if (!IsOpen)
            throw new TeamDomainException(TeamError.InvalidTaskTransition, "Una tarea completada no puede editarse.");
        ScheduledFor = scheduledFor;
        if (!string.IsNullOrWhiteSpace(description))
        {
            var clean = description.Trim();
            Description = clean.Length > DescriptionMaxLength ? clean[..DescriptionMaxLength] : clean;
        }
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
