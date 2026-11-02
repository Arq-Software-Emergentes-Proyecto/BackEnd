using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using BuildingFex.Api.Team.Domain.Model.Aggregates;
using BuildingFex.Api.Team.Domain.Model.ValueObjects;
using BuildingFex.Api.Team.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Team.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class TeamWorkerRepository(AppDbContext context) : BaseRepository<TeamWorker>(context), ITeamWorkerRepository
{
    public async Task<TeamWorker?> FindByIdentifierAsync(string id, CancellationToken ct = default)
    {
        var clean = (id ?? string.Empty).Trim();
        if (clean.Length == 0) return null;
        return await Context.Set<TeamWorker>()
            .FirstOrDefaultAsync(w => w.ExternalId == clean || w.Id.ToString() == clean, ct);
    }

    public async Task<TeamWorker?> FindInTenantAsync(string ownerAdminId, string id, CancellationToken ct = default)
    {
        var clean = (id ?? string.Empty).Trim();
        if (clean.Length == 0) return null;
        return await Context.Set<TeamWorker>()
            .FirstOrDefaultAsync(w => w.OwnerAdminId == ownerAdminId
                                      && (w.ExternalId == clean || w.Id.ToString() == clean), ct);
    }

    public async Task<IReadOnlyList<TeamWorker>> ListByOwnerAsync(
        string ownerAdminId,
        string? status = null,
        CancellationToken ct = default)
    {
        var query = Context.Set<TeamWorker>().Where(w => w.OwnerAdminId == ownerAdminId);
        if (status == StaffStatus.Active)
            query = query.Where(w => w.Status != StaffStatus.Inactive);
        else if (status == StaffStatus.Inactive)
            query = query.Where(w => w.Status == StaffStatus.Inactive);
        return await query.OrderByDescending(w => w.CreatedAt).ToListAsync(ct);
    }
}
