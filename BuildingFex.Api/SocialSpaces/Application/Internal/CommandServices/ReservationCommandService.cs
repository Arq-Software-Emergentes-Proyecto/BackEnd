using System.Security.Cryptography;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.SocialSpaces.Application.Internal.OutboundServices;
using BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;
using BuildingFex.Api.SocialSpaces.Domain.Model.ValueObjects;
using BuildingFex.Api.SocialSpaces.Domain.Repositories;
using BuildingFex.Api.SocialSpaces.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.SocialSpaces.Application.Internal.CommandServices;

public enum ReservationErrorKind
{
    Validation,
    NotFound,
    Forbidden,
    Conflict,
    Gone,
}

public sealed record ReservationError(ReservationErrorKind Kind, string Code, string Message);

public sealed record ReservationResult(Reservation? Reservation, ReservationError? Error)
{
    public static ReservationResult Ok(Reservation r) => new(r, null);
    public static ReservationResult Fail(ReservationErrorKind kind, string code, string message) =>
        new(null, new ReservationError(kind, code, message));
}

public sealed record CreateReservationCommand(
    int OwnerAdminId,
    string SpaceExternalId,
    string ResidentExternalId,
    string ResidentName,
    string ResidentCode,
    string Date,
    string StartTime,
    string EndTime,
    string? GuestsJson,
    bool EnforceDelinquencyRule = true);

/// <summary>
/// Casos de uso de reservas (Tabla 70): RequestReservation, CancelReservation, GenerateGuestPass, CheckInGuest.
/// La creación es atómica: transacción + lock del espacio (SELECT ... FOR UPDATE) + índice único de turno activo.
/// </summary>
public class ReservationCommandService(
    IReservationRepository reservationRepository,
    ISocialSpaceRepository socialSpaceRepository,
    IUnitOfWork unitOfWork,
    ReservationPolicy policy,
    IAccountStatusAcl accountStatus,
    SocialSpacesClock clock,
    ILogger<ReservationCommandService> logger)
{
    public async Task<ReservationResult> CreateAsync(CreateReservationCommand command, CancellationToken ct)
    {
        var slot = TimeSlot.TryCreate(command.Date, command.StartTime, command.EndTime);
        if (slot is null)
            return ReservationResult.Fail(ReservationErrorKind.Validation, "RESERVATION_TIME_INVALID", "Horario inválido.");

        // Reglamento: un residente moroso no reserva hasta regularizar (consulta EstadoDeCuenta a Finances).
        if (command.EnforceDelinquencyRule
            && await accountStatus.BlocksReservationsAsync(command.OwnerAdminId, command.ResidentExternalId, ct))
            return FromViolation(ReservationViolation.ResidentDelinquent);

        var now = clock.UtcNow;
        var guestsJson = GuestListSanitizer.Sanitize(command.GuestsJson, "[]", allowCheckInChanges: false, now);
        var guestCount = Reservation.CountGuests(guestsJson);

        try
        {
            await using var booking = await reservationRepository.BeginBookingAsync(
                command.OwnerAdminId, command.SpaceExternalId, ct);
            if (booking is null)
                return ReservationResult.Fail(ReservationErrorKind.NotFound, "SPACE_NOT_FOUND", "Espacio no encontrado.");

            var space = await socialSpaceRepository.FindByExternalIdAsync(command.SpaceExternalId, ct);
            if (space is null || space.OwnerAdminId != command.OwnerAdminId)
                return ReservationResult.Fail(ReservationErrorKind.NotFound, "SPACE_NOT_FOUND", "Espacio no encontrado.");

            var sameDay = await reservationRepository.FindBySpaceAndDateAsync(
                command.OwnerAdminId, space.ExternalId, slot.DateKey, ct);

            var verdict = policy.CanReserve(space, slot, guestCount, sameDay);
            if (!verdict.IsAllowed)
                return FromViolation(verdict.Violation);

            var reservation = Reservation.Create(
                NewReservationId(),
                command.OwnerAdminId,
                space.ExternalId,
                command.ResidentExternalId,
                command.ResidentName,
                command.ResidentCode,
                slot.DateKey,
                slot.StartKey,
                slot.EndKey,
                guestsJson,
                null);

            if (guestCount > 0)
                reservation.IssueGuestInvite(NewGuestInviteToken(), reservation.ComputeGuestInviteExpiry(clock.BuildingTimeZone));

            await reservationRepository.AddAsync(reservation, ct);
            await unitOfWork.CompleteAsync(ct);
            await booking.CommitAsync(ct);
            return ReservationResult.Ok(reservation);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Respaldo del lock: el índice único ux_reservations_owner_slot rechazó un turno duplicado.
            logger.LogInformation("Reservation slot conflict on {Space} {Date} {Start}", command.SpaceExternalId, slot.DateKey, slot.StartKey);
            return FromViolation(ReservationViolation.Overlap);
        }
    }

    /// <summary>Reemplaza la lista de invitados y emite/retira el pase QR (token generado por el servidor).</summary>
    public async Task<ReservationResult> UpdateGuestsAsync(
        Reservation reservation,
        string incomingGuestsJson,
        bool allowCheckInChanges,
        CancellationToken ct)
    {
        if (!reservation.IsActive)
            return ReservationResult.Fail(ReservationErrorKind.Conflict, "RESERVATION_CANCELLED", "La reserva está cancelada.");

        var now = clock.UtcNow;
        var guestsJson = GuestListSanitizer.Sanitize(incomingGuestsJson, reservation.GuestsJson, allowCheckInChanges, now);

        if (allowCheckInChanges)
        {
            var before = reservation.CheckedInGuestIds();
            var newlyCheckedIn = Reservation.ReadCheckedInIds(guestsJson).Except(before).Any();
            if (newlyCheckedIn)
            {
                var passStatus = reservation.GuestPassStatusAt(now, clock.BuildingTimeZone);
                if (passStatus == GuestPassStatus.NotYetValid) return GuestPassNotYetValid(reservation);
                if (passStatus != GuestPassStatus.Valid) return GuestPassExpired();
            }
        }

        var space = await socialSpaceRepository.FindByExternalIdAsync(reservation.SpaceExternalId, ct);
        var guestCount = Reservation.CountGuests(guestsJson);
        var verdict = policy.CanHostGuests(space, guestCount);
        if (!verdict.IsAllowed)
            return FromViolation(verdict.Violation);

        if (guestCount > 0)
        {
            reservation.PatchGuests(guestsJson, reservation.GuestInviteToken ?? NewGuestInviteToken());
            reservation.IssueGuestInvite(
                reservation.GuestInviteToken!,
                reservation.ComputeGuestInviteExpiry(clock.BuildingTimeZone));
        }
        else
        {
            reservation.PatchGuests(guestsJson, null);
        }

        reservationRepository.Update(reservation);
        await unitOfWork.CompleteAsync(ct);
        return ReservationResult.Ok(reservation);
    }

    /// <summary>Cancela la reserva y libera el turno (la fila queda en el historial).</summary>
    public async Task<ReservationResult> CancelAsync(Reservation reservation, CancellationToken ct)
    {
        reservation.Cancel(clock.UtcNow);
        reservationRepository.Update(reservation);
        await unitOfWork.CompleteAsync(ct);
        return ReservationResult.Ok(reservation);
    }

    /// <summary>Borrado físico (admin al eliminar un espacio o residente).</summary>
    public async Task PurgeAsync(Reservation reservation, CancellationToken ct)
    {
        reservationRepository.Remove(reservation);
        await unitOfWork.CompleteAsync(ct);
    }

    /// <summary>
    /// Portería: valida el QR y registra el ingreso. Rechaza pases expirados (fin del evento + 2 h) o de reservas canceladas.
    /// Sin guestId registra a todos los invitados del pase.
    /// </summary>
    public async Task<ReservationResult> CheckInByTokenAsync(
        string token,
        string? guestId,
        bool checkedIn,
        int tenantId,
        CancellationToken ct)
    {
        var reservation = await reservationRepository.FindByGuestInviteTokenAsync(token, ct);
        if (reservation is null)
            return ReservationResult.Fail(ReservationErrorKind.NotFound, "GUEST_PASS_NOT_FOUND", "Invitación no encontrada.");
        if (reservation.OwnerAdminId != tenantId)
            return ReservationResult.Fail(ReservationErrorKind.Forbidden, "FORBIDDEN_TENANT", "No tienes acceso a este recurso.");

        var now = clock.UtcNow;
        var status = reservation.GuestPassStatusAt(now, clock.BuildingTimeZone);
        if (checkedIn && status == GuestPassStatus.Cancelled)
            return ReservationResult.Fail(ReservationErrorKind.Gone, "GUEST_PASS_CANCELLED", "La reserva de esta invitación fue cancelada.");
        if (checkedIn && status == GuestPassStatus.NotYetValid)
            return GuestPassNotYetValid(reservation);
        if (checkedIn && status != GuestPassStatus.Valid)
            return GuestPassExpired();

        var ids = string.IsNullOrWhiteSpace(guestId)
            ? ReadGuestIds(reservation.GuestsJson)
            : [guestId.Trim()];
        if (ids.Count == 0)
            return ReservationResult.Fail(ReservationErrorKind.NotFound, "GUEST_NOT_FOUND", "Invitado no encontrado.");

        foreach (var id in ids)
        {
            if (!reservation.SetGuestCheckedIn(id, checkedIn, now))
                return ReservationResult.Fail(ReservationErrorKind.NotFound, "GUEST_NOT_FOUND", "Invitado no encontrado.");
        }

        reservationRepository.Update(reservation);
        await unitOfWork.CompleteAsync(ct);
        return ReservationResult.Ok(reservation);
    }

    private static List<string> ReadGuestIds(string guestsJson)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(guestsJson) ? "[]" : guestsJson)
                is System.Text.Json.Nodes.JsonArray arr
                ? arr.OfType<System.Text.Json.Nodes.JsonObject>()
                    .Select(g => g["id"]?.ToString())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToList()
                : [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>409: el pase solo vale el día de la reserva, desde 1 h antes del inicio.</summary>
    private static ReservationResult GuestPassNotYetValid(Reservation reservation) =>
        ReservationResult.Fail(
            ReservationErrorKind.Conflict,
            "GUEST_PASS_NOT_YET_VALID",
            $"La invitación aún no es válida: el ingreso se habilita el {reservation.Date} desde 1 hora antes de las {reservation.StartTime}.");

    private static ReservationResult GuestPassExpired() =>
        ReservationResult.Fail(ReservationErrorKind.Gone, "GUEST_PASS_EXPIRED", "Invitación Expirada.");

    private static ReservationResult FromViolation(ReservationViolation violation) => violation switch
    {
        ReservationViolation.SpaceUnderMaintenance => ReservationResult.Fail(
            ReservationErrorKind.Conflict, "SPACE_UNDER_MAINTENANCE", "El espacio está en mantenimiento."),
        ReservationViolation.Overlap => ReservationResult.Fail(
            ReservationErrorKind.Conflict, "RESERVATION_OVERLAP", "Traslape de horario: la reserva se cruza con otra existente."),
        ReservationViolation.DailyLimitReached => ReservationResult.Fail(
            ReservationErrorKind.Conflict, "RESERVATION_DAY_FULL", "El espacio alcanzó su límite de reservas para esa fecha."),
        ReservationViolation.CapacityExceeded => ReservationResult.Fail(
            ReservationErrorKind.Conflict, "RESERVATION_CAPACITY_EXCEEDED", "La cantidad de personas supera el aforo del espacio."),
        ReservationViolation.ResidentDelinquent => ReservationResult.Fail(
            ReservationErrorKind.Conflict, "RESIDENT_DELINQUENT",
            "Tienes una cuota vencida hace más de 30 días. El reglamento del edificio no permite reservar áreas comunes hasta regularizar el pago."),
        ReservationViolation.TooManyGuests => ReservationResult.Fail(
            ReservationErrorKind.Validation, "RESERVATION_TOO_MANY_GUESTS", $"Máximo {ReservationPolicy.MaxGuests} invitados por reserva."),
        _ => ReservationResult.Fail(ReservationErrorKind.Validation, "RESERVATION_INVALID", "Reserva inválida."),
    };

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is MySql.Data.MySqlClient.MySqlException { Number: 1062 })
                return true;
        }
        return false;
    }

    private static string NewReservationId() =>
        $"reservation-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";

    /// <summary>Token secreto del pase (128 bits), nunca elegido por el cliente.</summary>
    public static string NewGuestInviteToken() =>
        $"invite-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
}
