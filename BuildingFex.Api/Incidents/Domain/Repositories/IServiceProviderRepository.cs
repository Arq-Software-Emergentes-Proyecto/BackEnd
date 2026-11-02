using BuildingFex.Api.Incidents.Domain.Model.Aggregates;

namespace BuildingFex.Api.Incidents.Domain.Repositories;

/// <summary>Lectura de proveedores técnicos del condominio (assigneeId de las incidencias).</summary>
public interface IServiceProviderRepository
{
    Task<BuildingServiceProvider?> FindByExternalIdAsync(
        string ownerAdminExternalId,
        string externalId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BuildingServiceProvider>> ListByOwnerAsync(
        string ownerAdminExternalId,
        CancellationToken cancellationToken = default);
}
