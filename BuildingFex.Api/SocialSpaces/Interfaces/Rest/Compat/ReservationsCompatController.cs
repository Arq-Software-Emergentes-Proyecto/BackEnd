using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.SocialSpaces.Application.Internal;
using BuildingFex.Api.SocialSpaces.Application.Internal.CommandServices;
using BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;
using BuildingFex.Api.SocialSpaces.Domain.Repositories;
using BuildingFex.Api.SocialSpaces.Interfaces.Rest.Transform;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BuildingFex.Api.SocialSpaces.Interfaces.Rest.Compat;

/// <summary>
/// Reservas de áreas comunes (RF026, TS005). Las reglas (traslape, aforo, cupo diario, mantenimiento)
/// viven en <see cref="Domain.Services.ReservationPolicy"/>; este controlador solo traduce HTTP ↔ comandos.
/// Conflictos de turno → 409 Conflict.
/// </summary>
[ApiController]
[Route("reservations")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class ReservationsCompatController(
    IReservationRepository reservationRepository,
    ReservationCommandService reservationCommands,
    SocialSpacesClock clock) : TenantScopedControllerBase
{
    /// <summary>
    /// - Anónimo con guestInviteToken: página pública del invitado (token secreto; compatibilidad con links ya compartidos).
    /// - Admin: reservas de su condominio.
    /// - Residente: con residentId → solo las suyas; por espacio/fecha → disponibilidad del edificio
    ///   con los datos personales de otros residentes ocultos.
    /// - Las canceladas se excluyen salvo includeCancelled=true (historial).
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? ownerAdminId,
        [FromQuery] string? spaceId,
        [FromQuery] string? residentId,
        [FromQuery] string? date,
        [FromQuery] string? guestInviteToken,
        [FromQuery] bool includeCancelled,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(guestInviteToken))
        {
            var byToken = await reservationRepository.ListAsync(null, null, null, null, guestInviteToken, ct);
            return Ok(byToken.Select(ToJson));
        }

        if (!(User.Identity?.IsAuthenticated ?? false))
            return Unauthorized(new { code = "UNAUTHORIZED", message = "Autenticación requerida." });

        if (!AuthorizationPolicies.HasRole(User, AuthorizationPolicies.RoleAdmin)
            && !AuthorizationPolicies.HasRole(User, AuthorizationPolicies.RoleResident))
            return Forbid();

        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;

        string? effectiveResidentId = residentId;
        if (caller!.IsResident && !string.IsNullOrWhiteSpace(residentId))
        {
            var scopeError = ResolveResidentScope(caller, residentId, out effectiveResidentId);
            if (scopeError is not null) return scopeError;
        }

        var reservations = await reservationRepository.ListAsync(
            caller.TenantExternalId,
            spaceId,
            effectiveResidentId,
            date,
            null,
            ct,
            includeCancelled);

        return Ok(reservations.Select(r => ToJsonFor(caller, r)));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id, CancellationToken ct)
    {
        var (_, reservation, error) = await LoadOwnedAsync(id, ct);
        if (error is not null) return error;

        return Ok(ToJson(reservation!));
    }

    /// <summary>TS005: 201 confirmada · 409 traslape / aforo / cupo diario / mantenimiento · 404 espacio inexistente.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(BodyString(body, "ownerAdminId"), ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        var spaceId = BodyString(body, "spaceId")?.Trim();
        var residentId = BodyString(body, "residentId")?.Trim();
        var reservationDate = BodyString(body, "date")?.Trim();
        var startTime = BodyString(body, "startTime")?.Trim();
        var endTime = BodyString(body, "endTime")?.Trim();
        var residentName = BodyString(body, "residentName") ?? string.Empty;
        var residentCode = BodyString(body, "residentCode") ?? string.Empty;

        if (caller.IsResident)
        {
            if (!string.IsNullOrWhiteSpace(residentId)
                && !string.Equals(residentId, caller.UserExternalId, StringComparison.Ordinal))
                return ForbiddenTenant(caller, "residentId", residentId);

            // Un residente solo reserva a su nombre (datos del servidor, no del body).
            residentId = caller.UserExternalId;
            residentName = caller.User.Name;
            residentCode = caller.User.Code ?? residentCode;
        }

        if (string.IsNullOrWhiteSpace(spaceId) ||
            string.IsNullOrWhiteSpace(residentId) ||
            string.IsNullOrWhiteSpace(reservationDate) ||
            string.IsNullOrWhiteSpace(startTime) ||
            string.IsNullOrWhiteSpace(endTime))
        {
            return BadRequest(new { code = "RESERVATION_FIELDS_REQUIRED", message = "Campos obligatorios incompletos." });
        }

        var guestsJson = body.TryGetProperty("guests", out var guestsProp) && guestsProp.ValueKind == JsonValueKind.Array
            ? guestsProp.GetRawText()
            : "[]";

        // El id y el token del pase los genera el servidor (se ignoran id/guestInviteToken del body).
        var result = await reservationCommands.CreateAsync(new CreateReservationCommand(
            owner.Id,
            spaceId,
            residentId,
            residentName,
            residentCode,
            reservationDate,
            startTime,
            endTime,
            guestsJson,
            // El administrador puede reservar a nombre de un residente moroso (decisión de la junta).
            EnforceDelinquencyRule: caller.IsResident), ct);

        if (result.Error is not null) return ToError(caller, result.Error, spaceId);

        var created = await reservationRepository.FindByExternalIdAsync(result.Reservation!.ExternalId, ct);
        return StatusCode(StatusCodes.Status201Created, ToJson(created ?? result.Reservation));
    }

    /// <summary>
    /// Residente: edita invitados de SU reserva (el check-in lo conserva el servidor).
    /// Admin: cualquier reserva de su condominio (incluye check-in en recepción; se rechaza si el pase expiró).
    /// El token del pase QR lo emite el servidor cuando hay invitados.
    /// </summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, reservation, error) = await LoadOwnedAsync(id, ct);
        if (error is not null) return error;

        if (!body.TryGetProperty("guests", out var guestsProp) || guestsProp.ValueKind != JsonValueKind.Array)
            return Ok(ToJson(reservation!));

        var result = await reservationCommands.UpdateGuestsAsync(
            reservation!,
            guestsProp.GetRawText(),
            allowCheckInChanges: caller!.IsAdmin,
            ct);
        if (result.Error is not null) return ToError(caller, result.Error, id);

        return Ok(ToJson(result.Reservation!));
    }

    /// <summary>Cancela la reserva (libera el turno, queda en el historial).</summary>
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(string id, CancellationToken ct)
    {
        var (_, reservation, error) = await LoadOwnedAsync(id, ct);
        if (error is not null) return error;

        var result = await reservationCommands.CancelAsync(reservation!, ct);
        return Ok(ToJson(result.Reservation!));
    }

    /// <summary>
    /// Compatibilidad: DELETE = cancelar. Un admin puede borrar físicamente con ?purge=true
    /// (p. ej. al eliminar un espacio o un residente).
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] bool purge, CancellationToken ct)
    {
        var (caller, reservation, error) = await LoadOwnedAsync(id, ct);
        if (error is not null) return error;

        if (purge && caller!.IsAdmin)
            await reservationCommands.PurgeAsync(reservation!, ct);
        else
            await reservationCommands.CancelAsync(reservation!, ct);
        return NoContent();
    }

    /// <summary>Carga una reserva verificando tenant y, para residentes, que sea propia.</summary>
    private async Task<(CallerContext? Caller, Reservation? Reservation, IActionResult? Error)> LoadOwnedAsync(
        string id,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return (null, null, error);

        var reservation = await reservationRepository.FindByExternalIdAsync(id, ct);
        if (reservation is null)
            return (caller, null, NotFound(new { code = "RESERVATION_NOT_FOUND", message = "Reserva no encontrada." }));

        if (!BelongsToTenant(caller!, reservation.OwnerAdminId)
            || (caller!.IsResident
                && !string.Equals(reservation.ResidentExternalId, caller.UserExternalId, StringComparison.Ordinal)))
            return (caller, null, ForbiddenTenant(caller!, "reservation", id));

        return (caller, reservation, null);
    }

    private object ToJson(Reservation r) => SocialSpacesCompatSerializer.ReservationToJson(r, clock.BuildingTimeZone);

    /// <summary>Residentes ven la ocupación del espacio, pero no quién reservó ni sus invitados.</summary>
    private object ToJsonFor(CallerContext caller, Reservation r)
    {
        if (caller.IsAdmin
            || string.Equals(r.ResidentExternalId, caller.UserExternalId, StringComparison.Ordinal))
            return ToJson(r);

        return SocialSpacesCompatSerializer.ReservationToRedactedJson(r);
    }

    private IActionResult ToError(CallerContext caller, ReservationError error, string? resourceId) => error.Kind switch
    {
        ReservationErrorKind.Validation => BadRequest(new { code = error.Code, message = error.Message }),
        ReservationErrorKind.NotFound => NotFound(new { code = error.Code, message = error.Message }),
        ReservationErrorKind.Forbidden => ForbiddenTenant(caller, "reservation", resourceId),
        ReservationErrorKind.Gone => StatusCode(StatusCodes.Status410Gone, new { code = error.Code, message = error.Message }),
        _ => Conflict(new { code = error.Code, message = error.Message }),
    };
}
