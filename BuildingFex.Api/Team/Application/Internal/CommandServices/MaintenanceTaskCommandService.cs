using BuildingFex.Api.Incidents.Application.CommandServices;
using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Commands;
using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;
using BuildingFex.Api.Incidents.Domain.Repositories;
using BuildingFex.Api.Shared.Application.Model;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Team.Domain.Model;
using BuildingFex.Api.Team.Domain.Model.Aggregates;
using BuildingFex.Api.Team.Domain.Model.ValueObjects;
using BuildingFex.Api.Team.Domain.Repositories;
using BuildingFex.Api.Team.Domain.Services;

namespace BuildingFex.Api.Team.Application.Internal.CommandServices;

public sealed record CreateTaskCommand(
    string OwnerAdminId,
    int TenantId,
    string? ExternalId,
    string StaffId,
    string? IncidentId,
    string Description,
    DateTimeOffset? ScheduledFor);

/// <summary>
/// Casos de uso de MaintenanceTask (Tabla 82): CreateTask (aplica WorkloadPolicy), Start, Complete y Reassign.
/// La incidencia de origen se valida dentro del mismo condominio (solo se guarda su id).
/// TareaCompletada → Incidents: al completar la última tarea abierta de una incidencia, la incidencia se resuelve.
/// </summary>
public class MaintenanceTaskCommandService(
    IMaintenanceTaskRepository tasks,
    ITeamWorkerRepository workers,
    IIncidentRepository incidents,
    IIncidentCommandService incidentCommands,
    WorkloadPolicy workloadPolicy,
    IUnitOfWork unitOfWork)
{
    public Task<IReadOnlyList<MaintenanceTask>> ListAsync(
        string ownerAdminId,
        string? staffId,
        string? status,
        string? incidentId,
        bool openOnly,
        CancellationToken ct = default) =>
        tasks.ListAsync(
            ownerAdminId,
            string.IsNullOrWhiteSpace(staffId) ? null : staffId.Trim(),
            MaintenanceTaskStatus.Normalize(status),
            string.IsNullOrWhiteSpace(incidentId) ? null : incidentId.Trim(),
            openOnly,
            ct);

    public Task<MaintenanceTask?> FindAsync(string ownerAdminId, string id, CancellationToken ct = default) =>
        tasks.FindInTenantAsync(ownerAdminId, id, ct);

    public async Task<Result<MaintenanceTask>> Handle(CreateTaskCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.StaffId) || string.IsNullOrWhiteSpace(command.Description))
            return Result<MaintenanceTask>.Failure(TeamError.TaskFieldsRequired, "staffId y description son obligatorios.");

        var worker = await workers.FindInTenantAsync(command.OwnerAdminId, command.StaffId, ct);
        if (worker is null)
            return Result<MaintenanceTask>.Failure(TeamError.WorkerNotFound, "Trabajador no encontrado.");

        string? incidentExternalId = null;
        if (!string.IsNullOrWhiteSpace(command.IncidentId))
        {
            var incident = await incidents.FindByExternalIdAsync(command.IncidentId.Trim(), ct);
            if (incident is null || incident.OwnerAdminId != command.TenantId)
                return Result<MaintenanceTask>.Failure(TeamError.IncidentNotFound, "Incidencia no encontrada.");
            incidentExternalId = incident.ExternalId;
        }

        var open = await tasks.CountOpenByStaffAsync(command.OwnerAdminId, worker.ExternalId, ct);
        var violation = workloadPolicy.CanTakeTask(worker, open);
        if (violation != WorkloadViolation.None)
            return TeamWorkerCommandService.WorkloadFailure<MaintenanceTask>(violation);

        MaintenanceTask task;
        try
        {
            task = MaintenanceTask.Create(
                string.IsNullOrWhiteSpace(command.ExternalId)
                    ? $"task-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Random.Shared.Next(1000, 9999)}"
                    : command.ExternalId.Trim(),
                command.OwnerAdminId,
                worker.ExternalId,
                command.Description,
                command.ScheduledFor?.ToUniversalTime(),
                incidentExternalId);
        }
        catch (TeamDomainException ex)
        {
            return Result<MaintenanceTask>.Failure(ex.Error, ex.Message);
        }

        await tasks.AddAsync(task, ct);
        await unitOfWork.CompleteAsync(ct);
        return Result<MaintenanceTask>.Success(task);
    }

    public Task<Result<MaintenanceTask>> StartAsync(string ownerAdminId, string id, CancellationToken ct = default) =>
        TransitionAsync(ownerAdminId, id, t => t.Start(), ct);

    public async Task<Result<MaintenanceTask>> CompleteAsync(string ownerAdminId, string id, CancellationToken ct = default)
    {
        var result = await TransitionAsync(ownerAdminId, id, t => t.Complete(), ct);
        if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Value!.IncidentId))
            await ResolveIncidentIfDoneAsync(ownerAdminId, result.Value, ct);
        return result;
    }

    /// <summary>
    /// La incidencia queda resuelta cuando ya no le quedan tareas abiertas. El comentario de resolución
    /// (RF031) es la tarea completada; si la incidencia ya estaba resuelta no se toca.
    /// </summary>
    private async Task ResolveIncidentIfDoneAsync(string ownerAdminId, MaintenanceTask task, CancellationToken ct)
    {
        var incident = await incidents.FindByExternalIdAsync(task.IncidentId!, ct);
        if (incident is null || incident.Status == IncidentStatuses.Resolved) return;

        var stillOpen = await tasks.ListAsync(ownerAdminId, null, null, incident.ExternalId, openOnly: true, ct);
        if (stillOpen.Count > 0) return;

        var worker = await workers.FindInTenantAsync(ownerAdminId, task.StaffId, ct);
        var who = string.IsNullOrWhiteSpace(worker?.Name) ? "el equipo de mantenimiento" : worker!.Name;
        await incidentCommands.Handle(
            new ResolveIncidentCommand(
                incident.ExternalId,
                $"Resuelta al completar la tarea de {who}: {task.Description}",
                new IncidentActor(null, "Equipo de mantenimiento", "system")),
            ct);
    }

    public async Task<Result<MaintenanceTask>> ReassignAsync(
        string ownerAdminId,
        string id,
        string staffId,
        CancellationToken ct = default)
    {
        var task = await tasks.FindInTenantAsync(ownerAdminId, id, ct);
        if (task is null)
            return Result<MaintenanceTask>.Failure(TeamError.TaskNotFound, "Tarea no encontrada.");
        if (!task.IsOpen)
            return Result<MaintenanceTask>.Failure(TeamError.InvalidTaskTransition, "Una tarea completada no puede reasignarse.");

        var worker = await workers.FindInTenantAsync(ownerAdminId, staffId, ct);
        if (worker is null)
            return Result<MaintenanceTask>.Failure(TeamError.WorkerNotFound, "Trabajador no encontrado.");
        if (worker.ExternalId == task.StaffId)
            return Result<MaintenanceTask>.Success(task);

        var open = await tasks.CountOpenByStaffAsync(ownerAdminId, worker.ExternalId, ct);
        var violation = workloadPolicy.CanTakeTask(worker, open);
        if (violation != WorkloadViolation.None)
            return TeamWorkerCommandService.WorkloadFailure<MaintenanceTask>(violation);

        task.Reassign(worker.ExternalId);
        await unitOfWork.CompleteAsync(ct);
        return Result<MaintenanceTask>.Success(task);
    }

    private async Task<Result<MaintenanceTask>> TransitionAsync(
        string ownerAdminId,
        string id,
        Action<MaintenanceTask> transition,
        CancellationToken ct)
    {
        var task = await tasks.FindInTenantAsync(ownerAdminId, id, ct);
        if (task is null)
            return Result<MaintenanceTask>.Failure(TeamError.TaskNotFound, "Tarea no encontrada.");
        try
        {
            transition(task);
        }
        catch (TeamDomainException ex)
        {
            return Result<MaintenanceTask>.Failure(ex.Error, ex.Message);
        }

        await unitOfWork.CompleteAsync(ct);
        return Result<MaintenanceTask>.Success(task);
    }
}
