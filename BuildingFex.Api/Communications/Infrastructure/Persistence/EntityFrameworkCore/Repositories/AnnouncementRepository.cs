using BuildingFex.Api.Communications.Domain.Model;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Communications.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class AnnouncementRepository(AppDbContext context)
    : BaseRepository<Announcement>(context), IAnnouncementRepository
{
    private IQueryable<Announcement> WithOwner() =>
        Context.Set<Announcement>().Include(a => a.OwnerAdmin);

    public async Task<Announcement?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        return await WithOwner()
            .FirstOrDefaultAsync(a => a.ExternalId == externalId.Trim(), cancellationToken);
    }

    public async Task<IEnumerable<Announcement>> ListByOwnerAdminIdAsync(
        int ownerAdminId,
        DateTimeOffset? activeAt = null,
        CancellationToken cancellationToken = default)
    {
        var query = WithOwner().AsNoTracking().Where(a => a.OwnerAdminId == ownerAdminId);

        if (activeAt is { } now)
        {
            // GetActiveAnnouncements: filtrado en BD (nunca se devuelve un vencido como vigente).
            query = query.Where(a =>
                a.Status == AnnouncementStatuses.Published
                && a.ExpiresAt != null
                && a.ExpiresAt > now);
        }

        return await query
            .OrderByDescending(a =>
                a.Priority == AnnouncementPriorities.Urgent ? 3
                : a.Priority == AnnouncementPriorities.Important ? 2
                : 1)
            .ThenByDescending(a => a.PublishedAt ?? a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AnyAnnouncementsAsync(CancellationToken cancellationToken = default)
        => await Context.Set<Announcement>().AnyAsync(cancellationToken);
}
