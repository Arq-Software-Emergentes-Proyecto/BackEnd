using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Team.Domain.Model.Entities;

namespace BuildingFex.Api.Team.Domain.Repositories;

public interface IShiftRepository : IBaseRepository<Shift>
{
    Task<Shift?> FindInTenantAsync(string ownerAdminId, string id, CancellationToken ct = default);

    Task<IReadOnlyList<Shift>> ListAsync(
        string ownerAdminId,
        string? staffId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct = default);

    /// <summary>Turnos del trabajador que se cruzan con [start, end).</summary>
    Task<IReadOnlyList<Shift>> FindOverlappingAsync(
        string ownerAdminId,
        string staffId,
        DateTimeOffset start,
        DateTimeOffset end,
        string? excludeExternalId,
        CancellationToken ct = default);

    Task<IReadOnlyList<Shift>> ListByStaffAsync(string ownerAdminId, string staffId, CancellationToken ct = default);
}
