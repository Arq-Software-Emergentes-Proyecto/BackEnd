using BuildingFex.Api.Profiles.Domain.Model.Aggregates;
using BuildingFex.Api.Profiles.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Profiles.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class BuildingRepository(AppDbContext context) : BaseRepository<Building>(context), IBuildingRepository
{
    public async Task<IReadOnlyList<Building>> ListByOwnerAdminIdAsync(int ownerAdminId, CancellationToken cancellationToken = default)
        => await Context.Set<Building>()
            .Where(b => b.OwnerAdminId == ownerAdminId)
            .OrderBy(b => b.Id)
            .ToListAsync(cancellationToken);

    public async Task<int> SumDepartmentsByOwnerAdminIdAsync(int ownerAdminId, CancellationToken cancellationToken = default)
        => await Context.Set<Building>()
            .Where(b => b.OwnerAdminId == ownerAdminId)
            .SumAsync(b => (int?)b.DepartmentCount, cancellationToken) ?? 0;
}
