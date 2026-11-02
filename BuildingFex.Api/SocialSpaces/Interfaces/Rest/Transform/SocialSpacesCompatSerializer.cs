using System.Text.Json;
using BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;

namespace BuildingFex.Api.SocialSpaces.Interfaces.Rest.Transform;

public static class SocialSpacesCompatSerializer
{
    public static object SpaceToJson(SocialSpace space) => new
    {
        id = space.ExternalId,
        name = space.Name,
        description = space.Description,
        capacity = space.Capacity,
        imageUrl = space.ImageUrl,
        rules = space.Rules,
        status = space.Status,
        maxReservationsPerDay = space.MaxReservationsPerDay,
        ownerAdminId = space.OwnerAdmin?.ExternalId ?? string.Empty,
    };

    public static object ReservationToJson(Reservation reservation, TimeZoneInfo buildingTimeZone)
    {
        object[] guests;
        try
        {
            guests = JsonSerializer.Deserialize<object[]>(
                string.IsNullOrWhiteSpace(reservation.GuestsJson) ? "[]" : reservation.GuestsJson) ?? [];
        }
        catch
        {
            guests = [];
        }

        return new
        {
            id = reservation.ExternalId,
            spaceId = reservation.SpaceExternalId,
            residentId = reservation.ResidentExternalId,
            residentName = reservation.ResidentName,
            residentCode = reservation.ResidentCode,
            date = reservation.Date,
            startTime = reservation.StartTime,
            endTime = reservation.EndTime,
            status = reservation.Status,
            cancelledAt = reservation.CancelledAt,
            createdAt = reservation.CreatedAt,
            guests,
            guestInviteToken = reservation.GuestInviteToken,
            guestInviteExpiresAt = reservation.EffectiveGuestInviteExpiry(buildingTimeZone),
            ownerAdminId = reservation.OwnerAdmin?.ExternalId ?? string.Empty,
        };
    }

    /// <summary>Ocupación de un turno sin datos del residente que reservó (vista de otros residentes).</summary>
    public static object ReservationToRedactedJson(Reservation r) => new
    {
        id = r.ExternalId,
        spaceId = r.SpaceExternalId,
        residentId = string.Empty,
        residentName = string.Empty,
        residentCode = string.Empty,
        date = r.Date,
        startTime = r.StartTime,
        endTime = r.EndTime,
        status = r.Status,
        guests = Array.Empty<object>(),
        guestInviteToken = (string?)null,
        ownerAdminId = r.OwnerAdmin?.ExternalId ?? string.Empty,
    };
}
