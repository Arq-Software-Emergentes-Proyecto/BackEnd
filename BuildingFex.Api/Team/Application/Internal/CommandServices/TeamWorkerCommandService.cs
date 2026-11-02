using BuildingFex.Api.Shared.Application.Model;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Team.Domain.Model;
using BuildingFex.Api.Team.Domain.Model.Aggregates;
using BuildingFex.Api.Team.Domain.Model.ValueObjects;
using BuildingFex.Api.Team.Domain.Repositories;
using BuildingFex.Api.Team.Domain.Services;

namespace BuildingFex.Api.Team.Application.Internal.CommandServices;

public sealed record RegisterStaffCommand(
    string OwnerAdminId,
    string? ExternalId,
    string Name,
    string Phone,
    string Dni,
    decimal Salary,
    string PhotoUrl,
    string? Position);

public sealed record UpdateStaffCommand(
    string OwnerAdminId,
    string StaffId,
    string? Name,
    string? Phone,
    string? Dni,
    decimal? Salary,
    string? PhotoUrl,
    string? Position);

/// <summary>Resultado de desactivar: trabajador + tareas abiertas reasignadas / que siguen a su nombre.</summary>
public sealed record DeactivateStaffResult(TeamWorker Worker, int ReassignedTasks, int OpenTasksLeft);

public sealed record StaffWorkload(TeamWorker Worker, int OpenTasks);

/// <summary>
/// Casos de uso de StaffMember (Tabla 82): RegisterStaff, DeactivateStaff (reasigna sus tareas abiertas
/// si se indica un reemplazo, aplicando WorkloadPolicy) y GetWorkload.
/// </summary>
public class TeamWorkerCommandService(
    ITeamWorkerRepository workers,
    IMaintenanceTaskRepository tasks,
    IShiftRepository shifts,
    WorkloadPolicy workloadPolicy,
    IUnitOfWork unitOfWork)
{
    public async Task<IReadOnlyList<StaffWorkload>> GetWorkloadAsync(
        string ownerAdminId,
        string? status,
        CancellationToken ct = default)
    {
        var list = await workers.ListByOwnerAsync(ownerAdminId, NormalizeStatusFilter(status), ct);
        var open = await tasks.CountOpenGroupedByStaffAsync(ownerAdminId, ct);
        return list
            .Select(w => new StaffWorkload(w, open.TryGetValue(w.ExternalId, out var n) ? n : 0))
            .ToList();
    }

    public async Task<int> CountOpenTasksAsync(string ownerAdminId, string staffExternalId, CancellationToken ct = default) =>
        await tasks.CountOpenByStaffAsync(ownerAdminId, staffExternalId, ct);

    public async Task<Result<TeamWorker>> Handle(RegisterStaffCommand command, CancellationToken ct = default)
    {
        string? position = null;
        if (!string.IsNullOrWhiteSpace(command.Position))
        {
            position = StaffPosition.Normalize(command.Position);
            if (position is null)
                return Result<TeamWorker>.Failure(TeamError.InvalidPosition,
                    $"Puesto no válido. Valores: {string.Join(", ", StaffPosition.All)}.");
        }

        var worker = TeamWorker.Create(
            string.IsNullOrWhiteSpace(command.ExternalId)
                ? $"worker-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
                : command.ExternalId.Trim(),
            command.OwnerAdminId,
            command.Name,
            command.Phone,
            command.Dni,
            command.Salary,
            command.PhotoUrl,
            position);

        await workers.AddAsync(worker, ct);
        await unitOfWork.CompleteAsync(ct);
        return Result<TeamWorker>.Success(worker);
    }

    public async Task<Result<TeamWorker>> Handle(UpdateStaffCommand command, CancellationToken ct = default)
    {
        var worker = await workers.FindInTenantAsync(command.OwnerAdminId, command.StaffId, ct);
        if (worker is null)
            return Result<TeamWorker>.Failure(TeamError.WorkerNotFound, "Trabajador no encontrado.");

        try
        {
            worker.UpdateDetails(command.Name, command.Phone, command.Dni, command.Salary, command.PhotoUrl, command.Position);
        }
        catch (TeamDomainException ex)
        {
            return Result<TeamWorker>.Failure(ex.Error, ex.Message);
        }

        await unitOfWork.CompleteAsync(ct);
        return Result<TeamWorker>.Success(worker);
    }

    public async Task<Result<DeactivateStaffResult>> DeactivateAsync(
        string ownerAdminId,
        string staffId,
        string? reassignToId,
        CancellationToken ct = default)
    {
        var worker = await workers.FindInTenantAsync(ownerAdminId, staffId, ct);
        if (worker is null)
            return Result<DeactivateStaffResult>.Failure(TeamError.WorkerNotFound, "Trabajador no encontrado.");

        var open = await tasks.FindOpenByStaffAsync(ownerAdminId, worker.ExternalId, ct);
        var reassigned = 0;

        if (!string.IsNullOrWhiteSpace(reassignToId) && open.Count > 0)
        {
            var target = await workers.FindInTenantAsync(ownerAdminId, reassignToId, ct);
            if (target is null || target.ExternalId == worker.ExternalId)
                return Result<DeactivateStaffResult>.Failure(TeamError.WorkerNotFound, "Trabajador de reemplazo no encontrado.");

            var targetOpen = await tasks.CountOpenByStaffAsync(ownerAdminId, target.ExternalId, ct);
            var violation = workloadPolicy.CanTakeTask(target, targetOpen, open.Count);
            if (violation != WorkloadViolation.None)
                return WorkloadFailure<DeactivateStaffResult>(violation);

            foreach (var task in open)
                task.Reassign(target.ExternalId);
            reassigned = open.Count;
        }

        worker.Deactivate();
        await unitOfWork.CompleteAsync(ct);
        return Result<DeactivateStaffResult>.Success(new DeactivateStaffResult(worker, reassigned, open.Count - reassigned));
    }

    public async Task<Result<TeamWorker>> ReactivateAsync(string ownerAdminId, string staffId, CancellationToken ct = default)
    {
        var worker = await workers.FindInTenantAsync(ownerAdminId, staffId, ct);
        if (worker is null)
            return Result<TeamWorker>.Failure(TeamError.WorkerNotFound, "Trabajador no encontrado.");
        worker.Reactivate();
        await unitOfWork.CompleteAsync(ct);
        return Result<TeamWorker>.Success(worker);
    }

    /// <summary>Borra el trabajador y sus turnos. Las tareas quedan como historial.</summary>
    public async Task<bool> DeleteAsync(TeamWorker worker, CancellationToken ct = default)
    {
        foreach (var shift in await shifts.ListByStaffAsync(worker.OwnerAdminId, worker.ExternalId, ct))
            shifts.Remove(shift);
        workers.Remove(worker);
        await unitOfWork.CompleteAsync(ct);
        return true;
    }

    internal static Result<T> WorkloadFailure<T>(WorkloadViolation violation) =>
        violation == WorkloadViolation.WorkerInactive
            ? Result<T>.Failure(TeamError.WorkerInactive, "El trabajador está inactivo y no puede recibir tareas.")
            : Result<T>.Failure(TeamError.WorkloadExceeded,
                $"El trabajador ya tiene el máximo de {WorkloadPolicy.MaxOpenTasksPerWorker} tareas abiertas.");

    private static string? NormalizeStatusFilter(string? status)
    {
        var clean = (status ?? string.Empty).Trim().ToUpperInvariant();
        return clean is StaffStatus.Active or StaffStatus.Inactive ? clean : null;
    }
}
