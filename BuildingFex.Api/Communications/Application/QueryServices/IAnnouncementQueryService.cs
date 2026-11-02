using BuildingFex.Api.Communications.Domain.Model.Aggregates;

namespace BuildingFex.Api.Communications.Application.QueryServices;

public interface IAnnouncementQueryService
{
    /// <summary>Todos los comunicados del condominio (incluye vencidos), por prioridad y fecha.</summary>
    Task<IEnumerable<Announcement>> ListByOwnerExternalIdAsync(
        string? ownerAdminExternalId,
        CancellationToken cancellationToken = default);

    /// <summary>GetActiveAnnouncements: solo vigentes (ExpiresAt &gt; ahora), por prioridad y fecha (RF019/RF020).</summary>
    Task<IEnumerable<Announcement>> ListActiveByOwnerExternalIdAsync(
        string ownerAdminExternalId,
        CancellationToken cancellationToken = default);
}
