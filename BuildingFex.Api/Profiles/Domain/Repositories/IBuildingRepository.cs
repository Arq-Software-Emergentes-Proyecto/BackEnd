using BuildingFex.Api.Profiles.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Repositories;

namespace BuildingFex.Api.Profiles.Domain.Repositories;

public interface IBuildingRepository : IBaseRepository<Building>
{
    Task<IReadOnlyList<Building>> ListByOwnerAdminIdAsync(int ownerAdminId, CancellationToken cancellationToken = default);
    Task<int> SumDepartmentsByOwnerAdminIdAsync(int ownerAdminId, CancellationToken cancellationToken = default);
}
