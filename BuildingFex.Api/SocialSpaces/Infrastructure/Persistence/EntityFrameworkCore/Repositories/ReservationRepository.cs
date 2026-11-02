using System.Data;
using BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;
using BuildingFex.Api.SocialSpaces.Domain.Model.ValueObjects;
using BuildingFex.Api.SocialSpaces.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BuildingFex.Api.SocialSpaces.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class ReservationRepository(AppDbContext context)
    : BaseRepository<Reservation>(context), IReservationRepository
{
    private IQueryable<Reservation> WithOwner() =>
        Context.Set<Reservation>().Include(r => r.OwnerAdmin);

    public async Task<Reservation?> FindByExternalIdAsync(
        string externalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        return await WithOwner()
            .FirstOrDefaultAsync(r => r.ExternalId == externalId.Trim(), cancellationToken);
    }

    public async Task<Reservation?> FindByGuestInviteTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var clean = token.Trim();
        return await WithOwner().FirstOrDefaultAsync(r => r.GuestInviteToken == clean, cancellationToken);
    }

    public async Task<IReadOnlyList<Reservation>> FindBySpaceAndDateAsync(
        int ownerAdminId,
        string spaceExternalId,
        string date,
        CancellationToken cancellationToken = default)
    {
        var space = spaceExternalId.Trim();
        var day = date.Trim();
        return await WithOwner()
            .Where(r => r.OwnerAdminId == ownerAdminId && r.SpaceExternalId == space && r.Date == day)
            .OrderBy(r => r.StartTime)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Reservation>> ListAsync(
        string? ownerAdminExternalId,
        string? spaceExternalId,
        string? residentExternalId,
        string? date,
        string? guestInviteToken,
        CancellationToken cancellationToken = default,
        bool includeCancelled = false)
    {
        var query = WithOwner().AsQueryable();

        if (!string.IsNullOrWhiteSpace(guestInviteToken))
            query = query.Where(r => r.GuestInviteToken == guestInviteToken.Trim());
        else
        {
            if (!string.IsNullOrWhiteSpace(ownerAdminExternalId))
                query = query.Where(r => r.OwnerAdmin!.ExternalId == ownerAdminExternalId.Trim());

            if (!string.IsNullOrWhiteSpace(spaceExternalId))
                query = query.Where(r => r.SpaceExternalId == spaceExternalId.Trim());

            if (!string.IsNullOrWhiteSpace(residentExternalId))
                query = query.Where(r => r.ResidentExternalId == residentExternalId.Trim());

            if (!string.IsNullOrWhiteSpace(date))
                query = query.Where(r => r.Date == date.Trim());

            if (!includeCancelled)
                query = query.Where(r => r.Status != ReservationStatus.Cancelled);
        }

        return await query.OrderBy(r => r.Date).ThenBy(r => r.StartTime).ToListAsync(cancellationToken);
    }

    public async Task<IReservationBookingScope?> BeginBookingAsync(
        int ownerAdminId,
        string spaceExternalId,
        CancellationToken cancellationToken = default)
    {
        // READ COMMITTED: tras obtener el lock, las lecturas siguientes ven lo que confirmó la transacción anterior.
        var tx = await Context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var locked = await Context.Database
                .SqlQueryRaw<int>(
                    "SELECT id AS `Value` FROM social_spaces WHERE owner_admin_id = {0} AND external_id = {1} FOR UPDATE",
                    ownerAdminId,
                    spaceExternalId.Trim())
                .ToListAsync(cancellationToken);

            if (locked.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                await tx.DisposeAsync();
                return null;
            }

            return new BookingScope(tx);
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default)
        => await Context.Set<Reservation>().AnyAsync(cancellationToken);

    private sealed class BookingScope(IDbContextTransaction tx) : IReservationBookingScope
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => tx.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => tx.DisposeAsync();
    }
}
