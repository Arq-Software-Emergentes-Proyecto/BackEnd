using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Repositories;

namespace BuildingFex.Api.Communications.Domain.Repositories;

public interface IAnnouncementRepository : IBaseRepository<Announcement>
{
    Task<Announcement?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Comunicados del condominio ordenados por prioridad (urgent &gt; important &gt; normal) y fecha desc (RF020).
    /// Con <paramref name="activeAt"/> solo devuelve los vigentes en ese instante (RF019 / RNF011).
    /// </summary>
    Task<IEnumerable<Announcement>> ListByOwnerAdminIdAsync(
        int ownerAdminId,
        DateTimeOffset? activeAt = null,
        CancellationToken cancellationToken = default);

    Task<bool> AnyAnnouncementsAsync(CancellationToken cancellationToken = default);
}
