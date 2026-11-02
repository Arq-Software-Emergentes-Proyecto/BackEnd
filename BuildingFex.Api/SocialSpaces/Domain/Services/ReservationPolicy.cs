using BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;
using BuildingFex.Api.SocialSpaces.Domain.Model.ValueObjects;

namespace BuildingFex.Api.SocialSpaces.Domain.Services;

public enum ReservationViolation
{
    None,
    SpaceUnderMaintenance,
    Overlap,
    DailyLimitReached,
    CapacityExceeded,
    TooManyGuests,
    /// <summary>Moroso y el reglamento del edificio restringe las reservas (lo decide Finances).</summary>
    ResidentDelinquent,
}

public sealed record ReservationPolicyResult(ReservationViolation Violation)
{
    public static readonly ReservationPolicyResult Allowed = new(ReservationViolation.None);
    public bool IsAllowed => Violation == ReservationViolation.None;
}

/// <summary>
/// Domain Service (Tabla 68): reglas para confirmar una reserva (RF026, RNF009).
/// - El espacio debe estar disponible (no en mantenimiento).
/// - No puede traslaparse con otra reserva activa del mismo espacio.
/// - No puede superar el cupo diario de reservas del espacio (si está configurado).
/// - Aforo: residente + invitados ≤ capacidad del espacio; máximo <see cref="MaxGuests"/> invitados.
/// </summary>
public sealed class ReservationPolicy
{
    public const int MaxGuests = 5;

    public ReservationPolicyResult CanReserve(
        SocialSpace space,
        TimeSlot slot,
        int guestCount,
        IEnumerable<Reservation> sameSpaceReservations,
        string? ignoreReservationExternalId = null)
    {
        if (!space.IsAvailable())
            return new(ReservationViolation.SpaceUnderMaintenance);

        var active = sameSpaceReservations
            .Where(r => r.IsActive
                        && !string.Equals(r.ExternalId, ignoreReservationExternalId, StringComparison.Ordinal))
            .ToList();

        if (active.Any(r => r.Slot is { } other && other.Overlaps(slot)))
            return new(ReservationViolation.Overlap);

        if (space.MaxReservationsPerDay is > 0
            && active.Count(r => string.Equals(r.Date, slot.DateKey, StringComparison.Ordinal)) >= space.MaxReservationsPerDay)
            return new(ReservationViolation.DailyLimitReached);

        return CanHostGuests(space, guestCount);
    }

    /// <summary>Aforo del espacio para el residente titular más sus invitados.</summary>
    public ReservationPolicyResult CanHostGuests(SocialSpace? space, int guestCount)
    {
        if (guestCount > MaxGuests)
            return new(ReservationViolation.TooManyGuests);

        if (space?.Capacity is > 0 && guestCount + 1 > space.Capacity)
            return new(ReservationViolation.CapacityExceeded);

        return ReservationPolicyResult.Allowed;
    }
}
