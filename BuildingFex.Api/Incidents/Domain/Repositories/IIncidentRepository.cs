using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Repositories;

namespace BuildingFex.Api.Incidents.Domain.Repositories;

public interface IIncidentRepository : IBaseRepository<Incident>
{
    /// <summary>Incluye el historial (Events) de la incidencia.</summary>
    Task<Incident?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken = default);

    Task<IEnumerable<Incident>> ListByOwnerAdminIdAsync(int ownerAdminId, CancellationToken cancellationToken = default);

    /// <summary>Filtro server-side por residente (RF018) y estado. Ordenado por fecha de reporte desc.</summary>
    Task<IEnumerable<Incident>> ListByOwnerAdminIdAsync(
        int ownerAdminId,
        string? residentExternalId,
        string? status,
        CancellationToken cancellationToken = default);

    Task<bool> AnyIncidentsAsync(CancellationToken cancellationToken = default);
}
