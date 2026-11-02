using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Team.Domain.Model.Aggregates;

namespace BuildingFex.Api.Team.Domain.Repositories;

public interface IMaintenanceTaskRepository : IBaseRepository<MaintenanceTask>
{
    Task<MaintenanceTask?> FindInTenantAsync(string ownerAdminId, string id, CancellationToken ct = default);

    Task<IReadOnlyList<MaintenanceTask>> ListAsync(
        string ownerAdminId,
        string? staffId,
        string? status,
        string? incidentId,
        bool openOnly,
        CancellationToken ct = default);

    /// <summary>Tareas abiertas (PENDING / IN_PROGRESS) del trabajador.</summary>
    Task<IReadOnlyList<MaintenanceTask>> FindOpenByStaffAsync(string ownerAdminId, string staffId, CancellationToken ct = default);

    Task<int> CountOpenByStaffAsync(string ownerAdminId, string staffId, CancellationToken ct = default);

    /// <summary>Carga de trabajo: tareas abiertas por trabajador del tenant.</summary>
    Task<IReadOnlyDictionary<string, int>> CountOpenGroupedByStaffAsync(string ownerAdminId, CancellationToken ct = default);
}
