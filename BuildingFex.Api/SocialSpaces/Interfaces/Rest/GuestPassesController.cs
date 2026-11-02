using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Shared.Interfaces.Rest;
using BuildingFex.Api.SocialSpaces.Application.Internal;
using BuildingFex.Api.SocialSpaces.Application.Internal.CommandServices;
using BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;
using BuildingFex.Api.SocialSpaces.Domain.Model.ValueObjects;
using BuildingFex.Api.SocialSpaces.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuildingFex.Api.SocialSpaces.Interfaces.Rest;

/// <summary>
/// Pases de visita con QR (RF027 / US027, Tabla 69). Un pase por reserva (token secreto compartido por sus invitados);
/// vence al terminar el evento + 2 h.
/// - GET  /api/v1/guest-passes/{token}          público: valida el pase (VALID / NOT_YET_VALID / EXPIRED / CANCELLED) sin datos privados.
/// - POST /api/v1/guest-passes/{token}/check-in admin (portería): registra el ingreso; 410 si el pase expiró,
///   409 GUEST_PASS_NOT_YET_VALID antes del día de la reserva / de 1 h antes del inicio.
/// </summary>
[ApiController]
[Route("api/v1/guest-passes")]
public class GuestPassesController(
    IReservationRepository reservationRepository,
    ISocialSpaceRepository socialSpaceRepository,
    ReservationCommandService reservationCommands,
    SocialSpacesClock clock) : TenantScopedControllerBase
{
    [HttpGet("{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> Validate(string token, CancellationToken ct)
    {
        var reservation = await reservationRepository.FindByGuestInviteTokenAsync(token, ct);
        if (reservation is null)
            return NotFound(new { code = "GUEST_PASS_NOT_FOUND", valid = false, status = "NOT_FOUND", message = "Invitación no encontrada." });

        return Ok(await ToPassJsonAsync(reservation, ct));
    }

    [HttpPost("{token}/check-in")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> CheckIn(string token, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var guestId = BodyString(body, "guestId");
        var checkedIn = !(body.ValueKind == JsonValueKind.Object
                          && body.TryGetProperty("checkedIn", out var c)
                          && c.ValueKind == JsonValueKind.False);

        var result = await reservationCommands.CheckInByTokenAsync(token, guestId, checkedIn, caller!.TenantId, ct);
        if (result.Error is { } e)
        {
            return e.Kind switch
            {
                ReservationErrorKind.Forbidden => ForbiddenTenant(caller, "guestPass", token),
                ReservationErrorKind.NotFound => NotFound(new { code = e.Code, message = e.Message }),
                ReservationErrorKind.Gone => StatusCode(StatusCodes.Status410Gone, new
                {
                    code = e.Code,
                    message = e.Message,
                    valid = false,
                    status = e.Code == "GUEST_PASS_CANCELLED" ? GuestPassStatus.Cancelled : GuestPassStatus.Expired,
                }),
                _ => Conflict(new
                {
                    code = e.Code,
                    message = e.Message,
                    valid = false,
                    status = e.Code == "GUEST_PASS_NOT_YET_VALID" ? GuestPassStatus.NotYetValid : null,
                }),
            };
        }

        return Ok(await ToPassJsonAsync(result.Reservation!, ct));
    }

    private async Task<object> ToPassJsonAsync(Reservation r, CancellationToken ct)
    {
        var space = await socialSpaceRepository.FindByExternalIdAsync(r.SpaceExternalId, ct);
        var status = r.GuestPassStatusAt(clock.UtcNow, clock.BuildingTimeZone);
        return new
        {
            valid = status == GuestPassStatus.Valid,
            status,
            validFrom = r.GuestPassValidFrom(clock.BuildingTimeZone),
            expiresAt = r.EffectiveGuestInviteExpiry(clock.BuildingTimeZone),
            reservation = new
            {
                id = r.ExternalId,
                spaceName = space?.Name ?? string.Empty,
                date = r.Date,
                startTime = r.StartTime,
                endTime = r.EndTime,
                status = r.Status,
                hostName = ShortName(r.ResidentName),
            },
            guests = PublicGuests(r.GuestsJson),
        };
    }

    /// <summary>"Ana María Pérez" → "Ana P." (sin apellido completo, unidad ni ids).</summary>
    private static string ShortName(string? fullName)
    {
        var parts = (fullName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return string.Empty;
        return parts.Length == 1 ? parts[0] : $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}.";
    }

    private static object[] PublicGuests(string guestsJson)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(guestsJson) ? "[]" : guestsJson) is JsonArray arr
                ? arr.OfType<JsonObject>()
                    .Select(g => (object)new
                    {
                        id = g["id"]?.ToString() ?? string.Empty,
                        name = g["name"]?.ToString() ?? string.Empty,
                        checkedIn = g["checkedIn"] is JsonValue v && v.TryGetValue<bool>(out var b) && b,
                        checkedInAt = g["checkedInAt"]?.ToString(),
                    })
                    .ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
