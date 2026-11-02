using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using BuildingFex.Api.Team.Domain.Model.Entities;
using BuildingFex.Api.Team.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Team.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class ShiftRepository(AppDbContext context) : BaseRepository<Shift>(context), IShiftRepository
{
    public async Task<Shift?> FindInTenantAsync(string ownerAdminId, string id, CancellationToken ct = default)
    {
        var clean = (id ?? string.Empty).Trim();
        if (clean.Length == 0) return null;
        return await Context.Set<Shift>()
            .FirstOrDefaultAsync(s => s.OwnerAdminId == ownerAdminId
                                      && (s.ExternalId == clean || s.Id.ToString() == clean), ct);
    }

    public async Task<IReadOnlyList<Shift>> ListAsync(
        string ownerAdminId,
        string? staffId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct = default)
    {
        var query = Context.Set<Shift>().Where(s => s.OwnerAdminId == ownerAdminId);
        if (!string.IsNullOrWhiteSpace(staffId))
            query = query.Where(s => s.StaffId == staffId);
        if (from.HasValue)
        {
            var f = from.Value.ToUniversalTime();
            query = query.Where(s => s.EndsAt > f);
        }
        if (to.HasValue)
        {
            var t = to.Value.ToUniversalTime();
            query = query.Where(s => s.StartsAt < t);
        }
        return await query.OrderBy(s => s.StartsAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Shift>> FindOverlappingAsync(
        string ownerAdminId,
        string staffId,
        DateTimeOffset start,
        DateTimeOffset end,
        string? excludeExternalId,
        CancellationToken ct = default)
    {
        // Prefiltro amplio en BD (turnos ≤ 24 h) y la regla exacta en el dominio (Shift.Overlaps).
        var s0 = start.ToUniversalTime().AddDays(-2);
        var e0 = end.ToUniversalTime().AddDays(2);
        var candidates = await Context.Set<Shift>()
            .Where(s => s.OwnerAdminId == ownerAdminId && s.StaffId == staffId
                        && s.StartsAt < e0 && s.EndsAt > s0)
            .ToListAsync(ct);
        return candidates
            .Where(s => !string.Equals(s.ExternalId, excludeExternalId, StringComparison.Ordinal))
            .Where(s => s.Overlaps(start, end))
            .ToList();
    }

    public async Task<IReadOnlyList<Shift>> ListByStaffAsync(string ownerAdminId, string staffId, CancellationToken ct = default) =>
        await Context.Set<Shift>()
            .Where(s => s.OwnerAdminId == ownerAdminId && s.StaffId == staffId)
            .ToListAsync(ct);
}
