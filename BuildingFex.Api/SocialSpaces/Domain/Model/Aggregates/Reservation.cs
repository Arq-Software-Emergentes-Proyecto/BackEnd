using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Model.Entities;
using BuildingFex.Api.SocialSpaces.Domain.Model.ValueObjects;

namespace BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;

/// <summary>
/// Reserva de un espacio común por un residente (Tabla 68).
/// Los invitados se guardan en <see cref="GuestsJson"/> y comparten un único pase/QR por reserva
/// (<see cref="GuestInviteToken"/>), que vence <see cref="GuestPassValidityAfterEnd"/> después del fin del evento (US027).
/// </summary>
public class Reservation : IAuditableEntity
{
    /// <summary>US027: "el evento terminó hace 2 horas" → la invitación ya está expirada.</summary>
    public static readonly TimeSpan GuestPassValidityAfterEnd = TimeSpan.FromHours(2);

    /// <summary>El pase se puede usar desde 1 h antes del inicio, y nunca antes del día de la reserva.</summary>
    public static readonly TimeSpan GuestPassValidityBeforeStart = TimeSpan.FromHours(1);

    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public User? OwnerAdmin { get; private set; }
    public string SpaceExternalId { get; private set; } = string.Empty;
    public string ResidentExternalId { get; private set; } = string.Empty;
    public string ResidentName { get; private set; } = string.Empty;
    public string ResidentCode { get; private set; } = string.Empty;
    public string Date { get; private set; } = string.Empty;
    public string StartTime { get; private set; } = string.Empty;
    public string EndTime { get; private set; } = string.Empty;
    public string GuestsJson { get; private set; } = "[]";
    public string? GuestInviteToken { get; private set; }
    public DateTimeOffset? GuestInviteExpiresAt { get; private set; }
    public string Status { get; private set; } = ReservationStatus.Confirmed;

    /// <summary>
    /// "space|date|start" mientras la reserva está activa; null al cancelarla.
    /// Índice único (owner_admin_id, slot_key): dos reservas activas no pueden ocupar el mismo turno (QAS02).
    /// </summary>
    public string? SlotKey { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private Reservation() { }

    public bool IsActive => !string.Equals(Status, ReservationStatus.Cancelled, StringComparison.Ordinal);

    /// <summary>Turno como Value Object (null para datos legados mal formados).</summary>
    public TimeSlot? Slot => TimeSlot.TryCreate(Date, StartTime, EndTime);

    public static Reservation Create(
        string externalId,
        int ownerAdminId,
        string spaceExternalId,
        string residentExternalId,
        string residentName,
        string residentCode,
        string date,
        string startTime,
        string endTime,
        string guestsJson,
        string? guestInviteToken)
    {
        var slot = TimeSlot.TryCreate(date, startTime, endTime);
        var reservation = new Reservation
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId,
            SpaceExternalId = spaceExternalId,
            ResidentExternalId = residentExternalId,
            ResidentName = residentName,
            ResidentCode = residentCode,
            Date = slot?.DateKey ?? date,
            StartTime = slot?.StartKey ?? startTime,
            EndTime = slot?.EndKey ?? endTime,
            GuestsJson = guestsJson,
            GuestInviteToken = guestInviteToken,
            Status = ReservationStatus.Confirmed,
        };
        reservation.SlotKey = BuildSlotKey(reservation.SpaceExternalId, reservation.Date, reservation.StartTime);
        return reservation;
    }

    public static string BuildSlotKey(string spaceExternalId, string date, string startTime) =>
        $"{spaceExternalId}|{date}|{startTime}";

    public void PatchGuests(string guestsJson, string? guestInviteToken)
    {
        GuestsJson = guestsJson;
        GuestInviteToken = guestInviteToken;
        if (guestInviteToken is null)
            GuestInviteExpiresAt = null;
    }

    /// <summary>Emite (o conserva) el pase de invitados y fija su vencimiento.</summary>
    public void IssueGuestInvite(string token, DateTimeOffset? expiresAt)
    {
        GuestInviteToken = token;
        GuestInviteExpiresAt = expiresAt;
    }

    /// <summary>Cancela y libera el turno (no borra el historial).</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (!IsActive) return;
        Status = ReservationStatus.Cancelled;
        SlotKey = null;
        CancelledAt = now;
    }

    /// <summary>Fin del evento + 2 h, en la zona horaria del condominio.</summary>
    public DateTimeOffset? ComputeGuestInviteExpiry(TimeZoneInfo buildingTimeZone)
    {
        var slot = Slot;
        if (slot is null) return null;
        var localExpiry = slot.LocalEnd.Add(GuestPassValidityAfterEnd);
        var offset = buildingTimeZone.GetUtcOffset(localExpiry);
        return new DateTimeOffset(DateTime.SpecifyKind(localExpiry, DateTimeKind.Unspecified), offset);
    }

    /// <summary>Vencimiento efectivo: el guardado o, para pases legados, el calculado.</summary>
    public DateTimeOffset? EffectiveGuestInviteExpiry(TimeZoneInfo buildingTimeZone) =>
        GuestInviteExpiresAt ?? ComputeGuestInviteExpiry(buildingTimeZone);

    /// <summary>
    /// Inicio de la ventana de ingreso: 1 h antes del inicio, pero no antes de las 00:00 del día de la reserva
    /// (hora del condominio). Null para datos legados sin turno legible (no se restringe).
    /// </summary>
    public DateTimeOffset? GuestPassValidFrom(TimeZoneInfo buildingTimeZone)
    {
        var slot = Slot;
        if (slot is null) return null;
        var dayStart = slot.Date.ToDateTime(TimeOnly.MinValue);
        var local = slot.LocalStart - GuestPassValidityBeforeStart;
        if (local < dayStart) local = dayStart;
        var offset = buildingTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
    }

    /// <summary>VALID / NOT_YET_VALID / EXPIRED / CANCELLED para el pase de esta reserva.</summary>
    public string GuestPassStatusAt(DateTimeOffset now, TimeZoneInfo buildingTimeZone)
    {
        if (!IsActive) return GuestPassStatus.Cancelled;
        var expiry = EffectiveGuestInviteExpiry(buildingTimeZone);
        if (expiry is null || now >= expiry.Value) return GuestPassStatus.Expired;
        if (GuestPassValidFrom(buildingTimeZone) is { } from && now < from) return GuestPassStatus.NotYetValid;
        return GuestPassStatus.Valid;
    }

    /// <summary>Registra (o revierte) el ingreso de un invitado. Devuelve false si el invitado no existe.</summary>
    public bool SetGuestCheckedIn(string guestId, bool checkedIn, DateTimeOffset now)
    {
        JsonArray guests;
        try
        {
            guests = JsonNode.Parse(string.IsNullOrWhiteSpace(GuestsJson) ? "[]" : GuestsJson) as JsonArray ?? [];
        }
        catch (JsonException)
        {
            return false;
        }

        var found = false;
        foreach (var g in guests.OfType<JsonObject>())
        {
            if (!string.Equals(g["id"]?.ToString(), guestId, StringComparison.Ordinal)) continue;
            found = true;
            var already = g["checkedIn"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            g["checkedIn"] = checkedIn;
            g["checkedInAt"] = checkedIn
                ? (already ? g["checkedInAt"]?.ToString() : now.UtcDateTime.ToString("o"))
                : null;
        }

        if (found)
            GuestsJson = guests.ToJsonString();
        return found;
    }

    /// <summary>Ids de invitados con ingreso registrado.</summary>
    public HashSet<string> CheckedInGuestIds() => ReadCheckedInIds(GuestsJson);

    public static HashSet<string> ReadCheckedInIds(string? guestsJson)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(guestsJson) ? "[]" : guestsJson) is JsonArray arr)
            {
                foreach (var g in arr.OfType<JsonObject>())
                {
                    var id = g["id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(id) && g["checkedIn"] is JsonValue v && v.TryGetValue<bool>(out var b) && b)
                        set.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // datos ilegibles → sin ingresos
        }
        return set;
    }

    public static int CountGuests(string? guestsJson)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(guestsJson) ? "[]" : guestsJson) is JsonArray arr
                ? arr.OfType<JsonObject>().Count()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
