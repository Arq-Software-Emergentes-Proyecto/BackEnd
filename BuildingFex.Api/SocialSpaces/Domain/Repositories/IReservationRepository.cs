using BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;

namespace BuildingFex.Api.SocialSpaces.Domain.Repositories;

public interface IReservationRepository
{
    Task<Reservation?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>Pase de invitados (QR) por su token secreto.</summary>
    Task<Reservation?> FindByGuestInviteTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Reservas (activas y canceladas) de un espacio en una fecha.</summary>
    Task<IReadOnlyList<Reservation>> FindBySpaceAndDateAsync(
        int ownerAdminId,
        string spaceExternalId,
        string date,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<Reservation>> ListAsync(
        string? ownerAdminExternalId,
        string? spaceExternalId,
        string? residentExternalId,
        string? date,
        string? guestInviteToken,
        CancellationToken cancellationToken = default,
        bool includeCancelled = false);

    /// <summary>
    /// Abre una transacción (READ COMMITTED) y bloquea la fila del espacio con SELECT ... FOR UPDATE,
    /// serializando las reservas concurrentes del mismo espacio (QAS02). False si el espacio no existe.
    /// </summary>
    Task<IReservationBookingScope?> BeginBookingAsync(
        int ownerAdminId,
        string spaceExternalId,
        CancellationToken cancellationToken = default);

    Task AddAsync(Reservation entity, CancellationToken cancellationToken = default);
    void Update(Reservation entity);
    void Remove(Reservation entity);
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Transacción de reserva con el espacio bloqueado. Dispose sin Commit = rollback.</summary>
public interface IReservationBookingScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}
