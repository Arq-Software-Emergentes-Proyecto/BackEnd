using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using BuildingFex.Api.Team.Domain.Model.Aggregates;
using BuildingFex.Api.Team.Domain.Model.ValueObjects;
using BuildingFex.Api.Team.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Team.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class MaintenanceTaskRepository(AppDbContext context)
    : BaseRepository<MaintenanceTask>(context), IMaintenanceTaskRepository
{
    public async Task<MaintenanceTask?> FindInTenantAsync(string ownerAdminId, string id, CancellationToken ct = default)
    {
        var clean = (id ?? string.Empty).Trim();
        if (clean.Length == 0) return null;
        return await Context.Set<MaintenanceTask>()
            .FirstOrDefaultAsync(t => t.OwnerAdminId == ownerAdminId
                                      && (t.ExternalId == clean || t.Id.ToString() == clean), ct);
    }

    public async Task<IReadOnlyList<MaintenanceTask>> ListAsync(
        string ownerAdminId,
        string? staffId,
        string? status,
        string? incidentId,
        bool openOnly,
        CancellationToken ct = default)
    {
        var query = Context.Set<MaintenanceTask>().Where(t => t.OwnerAdminId == ownerAdminId);
        if (!string.IsNullOrWhiteSpace(staffId))
            query = query.Where(t => t.StaffId == staffId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(t => t.Status == status);
        if (!string.IsNullOrWhiteSpace(incidentId))
            query = query.Where(t => t.IncidentId == incidentId);
        if (openOnly)
            query = query.Where(t => t.Status == MaintenanceTaskStatus.Pending || t.Status == MaintenanceTaskStatus.InProgress);

        return await query.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MaintenanceTask>> FindOpenByStaffAsync(
        string ownerAdminId,
        string staffId,
        CancellationToken ct = default) =>
        await ListAsync(ownerAdminId, staffId, null, null, openOnly: true, ct);

    public Task<int> CountOpenByStaffAsync(string ownerAdminId, string staffId, CancellationToken ct = default) =>
        Context.Set<MaintenanceTask>().CountAsync(
            t => t.OwnerAdminId == ownerAdminId
                 && t.StaffId == staffId
                 && (t.Status == MaintenanceTaskStatus.Pending || t.Status == MaintenanceTaskStatus.InProgress),
            ct);

    public async Task<IReadOnlyDictionary<string, int>> CountOpenGroupedByStaffAsync(
        string ownerAdminId,
        CancellationToken ct = default)
    {
        var rows = await Context.Set<MaintenanceTask>()
            .Where(t => t.OwnerAdminId == ownerAdminId
                        && (t.Status == MaintenanceTaskStatus.Pending || t.Status == MaintenanceTaskStatus.InProgress))
            .GroupBy(t => t.StaffId)
            .Select(g => new { StaffId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.StaffId, r => r.Count, StringComparer.Ordinal);
    }
}
