using BuildingFex.Api.Team.Domain.Model.Aggregates;

namespace BuildingFex.Api.Team.Domain.Services;

public enum WorkloadViolation
{
    None,
    WorkerInactive,
    MaxOpenTasksReached,
}

/// <summary>
/// Domain Service (Tabla 80 – WorkloadPolicy): un trabajador inactivo no recibe tareas y nadie
/// acumula más de <see cref="MaxOpenTasksPerWorker"/> tareas abiertas (PENDING o IN_PROGRESS).
/// También decide si un trabajador puede absorber las tareas de otro al desactivarlo.
/// </summary>
public sealed class WorkloadPolicy
{
    public const int MaxOpenTasksPerWorker = 10;

    public WorkloadViolation CanTakeTask(TeamWorker worker, int currentOpenTasks, int additionalTasks = 1)
    {
        if (!worker.IsActive)
            return WorkloadViolation.WorkerInactive;
        if (currentOpenTasks + Math.Max(additionalTasks, 0) > MaxOpenTasksPerWorker)
            return WorkloadViolation.MaxOpenTasksReached;
        return WorkloadViolation.None;
    }
}
