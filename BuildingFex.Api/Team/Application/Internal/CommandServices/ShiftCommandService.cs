using BuildingFex.Api.Shared.Application.Model;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Team.Domain.Model;
using BuildingFex.Api.Team.Domain.Model.Entities;
using BuildingFex.Api.Team.Domain.Repositories;

namespace BuildingFex.Api.Team.Application.Internal.CommandServices;

/// <summary>Turnos: un trabajador no puede tener dos turnos que se crucen (409 SHIFT_OVERLAP).</summary>
public class ShiftCommandService(
    IShiftRepository shifts,
    ITeamWorkerRepository workers,
    IUnitOfWork unitOfWork)
{
    public Task<IReadOnlyList<Shift>> ListAsync(
        string ownerAdminId,
        string? staffId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct = default) =>
        shifts.ListAsync(ownerAdminId, string.IsNullOrWhiteSpace(staffId) ? null : staffId.Trim(), from, to, ct);

    public Task<Shift?> FindAsync(string ownerAdminId, string id, CancellationToken ct = default) =>
        shifts.FindInTenantAsync(ownerAdminId, id, ct);

    public async Task<Result<Shift>> CreateAsync(
        string ownerAdminId,
        string? externalId,
        string staffId,
        DateTimeOffset start,
        DateTimeOffset end,
        string? notes,
        CancellationToken ct = default)
    {
        var worker = await workers.FindInTenantAsync(ownerAdminId, staffId, ct);
        if (worker is null)
            return Result<Shift>.Failure(TeamError.WorkerNotFound, "Trabajador no encontrado.");
        if (!worker.IsActive)
            return Result<Shift>.Failure(TeamError.WorkerInactive, "El trabajador está inactivo.");

        Shift shift;
        try
        {
            shift = Shift.Create(
                string.IsNullOrWhiteSpace(externalId)
                    ? $"shift-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Random.Shared.Next(1000, 9999)}"
                    : externalId.Trim(),
                ownerAdminId,
                worker.ExternalId,
                start,
                end,
                notes);
        }
        catch (TeamDomainException ex)
        {
            return Result<Shift>.Failure(ex.Error, ex.Message);
        }

        var overlapping = await shifts.FindOverlappingAsync(ownerAdminId, worker.ExternalId, shift.StartsAt, shift.EndsAt, null, ct);
        if (overlapping.Count > 0)
            return Result<Shift>.Failure(TeamError.ShiftOverlap, "El turno se cruza con otro turno del mismo trabajador.");

        await shifts.AddAsync(shift, ct);
        await unitOfWork.CompleteAsync(ct);
        return Result<Shift>.Success(shift);
    }

    public async Task<Result<Shift>> RescheduleAsync(
        string ownerAdminId,
        string id,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? notes,
        CancellationToken ct = default)
    {
        var shift = await shifts.FindInTenantAsync(ownerAdminId, id, ct);
        if (shift is null)
            return Result<Shift>.Failure(TeamError.ShiftNotFound, "Turno no encontrado.");

        var newStart = start ?? shift.StartsAt;
        var newEnd = end ?? shift.EndsAt;
        try
        {
            Shift.EnsureValidRange(newStart, newEnd);
        }
        catch (TeamDomainException ex)
        {
            return Result<Shift>.Failure(ex.Error, ex.Message);
        }

        var overlapping = await shifts.FindOverlappingAsync(ownerAdminId, shift.StaffId, newStart, newEnd, shift.ExternalId, ct);
        if (overlapping.Count > 0)
            return Result<Shift>.Failure(TeamError.ShiftOverlap, "El turno se cruza con otro turno del mismo trabajador.");

        shift.Reschedule(newStart, newEnd, notes);
        await unitOfWork.CompleteAsync(ct);
        return Result<Shift>.Success(shift);
    }

    public async Task<bool> DeleteAsync(string ownerAdminId, string id, CancellationToken ct = default)
    {
        var shift = await shifts.FindInTenantAsync(ownerAdminId, id, ct);
        if (shift is null) return false;
        shifts.Remove(shift);
        await unitOfWork.CompleteAsync(ct);
        return true;
    }
}
