using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Incidents.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class ServiceProviderRepository(AppDbContext context) : IServiceProviderRepository
{
    public async Task<BuildingServiceProvider?> FindByExternalIdAsync(
        string ownerAdminExternalId,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        var id = externalId.Trim();
        return await context.Set<BuildingServiceProvider>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OwnerAdminId == ownerAdminExternalId && p.ExternalId == id, cancellationToken);
    }

    public async Task<IReadOnlyList<BuildingServiceProvider>> ListByOwnerAsync(
        string ownerAdminExternalId,
        CancellationToken cancellationToken = default)
        => await context.Set<BuildingServiceProvider>()
            .AsNoTracking()
            .Where(p => p.OwnerAdminId == ownerAdminExternalId)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
}
