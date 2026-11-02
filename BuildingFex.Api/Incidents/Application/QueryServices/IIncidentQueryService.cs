using BuildingFex.Api.Incidents.Domain.Model.Aggregates;

namespace BuildingFex.Api.Incidents.Application.QueryServices;

public interface IIncidentQueryService
{
    Task<IEnumerable<Incident>> ListByOwnerExternalIdAsync(
        string? ownerAdminExternalId,
        CancellationToken cancellationToken = default);

    /// <summary>Lista del condominio filtrada en BD por residente y/o estado (RF018).</summary>
    Task<IEnumerable<Incident>> ListAsync(
        string ownerAdminExternalId,
        string? residentExternalId,
        string? status,
        CancellationToken cancellationToken = default);
}
