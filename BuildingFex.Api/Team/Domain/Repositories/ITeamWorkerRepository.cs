using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Team.Domain.Model.Aggregates;

namespace BuildingFex.Api.Team.Domain.Repositories;

public interface ITeamWorkerRepository : IBaseRepository<TeamWorker>
{
    /// <summary>Busca por external_id o id interno (sin filtrar tenant: el llamador valida pertenencia).</summary>
    Task<TeamWorker?> FindByIdentifierAsync(string id, CancellationToken ct = default);

    Task<TeamWorker?> FindInTenantAsync(string ownerAdminId, string id, CancellationToken ct = default);

    Task<IReadOnlyList<TeamWorker>> ListByOwnerAsync(string ownerAdminId, string? status = null, CancellationToken ct = default);
}
