using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Incidents.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class IncidentRepository(AppDbContext context)
    : BaseRepository<Incident>(context), IIncidentRepository
{
    private IQueryable<Incident> WithOwner() =>
        Context.Set<Incident>().Include(i => i.OwnerAdmin);

    public async Task<Incident?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        var id = externalId.Trim();
        return await WithOwner()
            .Include(i => i.Events)
            .FirstOrDefaultAsync(i => i.ExternalId == id, cancellationToken);
    }

    public Task<IEnumerable<Incident>> ListByOwnerAdminIdAsync(
        int ownerAdminId,
        CancellationToken cancellationToken = default)
        => ListByOwnerAdminIdAsync(ownerAdminId, null, null, cancellationToken);

    public async Task<IEnumerable<Incident>> ListByOwnerAdminIdAsync(
        int ownerAdminId,
        string? residentExternalId,
        string? status,
        CancellationToken cancellationToken = default)
    {
        var query = WithOwner().AsNoTracking().Where(i => i.OwnerAdminId == ownerAdminId);

        if (!string.IsNullOrWhiteSpace(residentExternalId))
        {
            var resident = residentExternalId.Trim();
            query = query.Where(i => i.ResidentExternalId == resident);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = IncidentStatuses.Normalize(status);
            query = query.Where(i => i.Status == normalized);
        }

        return await query
            .OrderByDescending(i => i.ReportedAt)
            .ThenByDescending(i => i.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Incident>> ListEscalationCandidatesAsync(
        DateTimeOffset reportedBefore,
        int limit,
        CancellationToken cancellationToken = default)
        => await WithOwner()
            .Include(i => i.Events)
            .Where(i => i.Status == IncidentStatuses.Open
                        && i.AssigneeId == null
                        && i.Provider == null
                        && i.EscalatedAt == null
                        && i.ReportedAt <= reportedBefore)
            .OrderBy(i => i.ReportedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<bool> AnyIncidentsAsync(CancellationToken cancellationToken = default)
        => await Context.Set<Incident>().AnyAsync(cancellationToken);
}
