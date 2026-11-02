namespace BuildingFex.Api.SocialSpaces.Domain.Model.ValueObjects;

/// <summary>Estado de la reserva (Tabla 68). Las reservas se confirman al crearse; PENDING queda reservado.</summary>
public static class ReservationStatus
{
    public const string Pending = "PENDING";
    public const string Confirmed = "CONFIRMED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>Estado operativo de un espacio común.</summary>
public static class SocialSpaceStatus
{
    public const string Available = "AVAILABLE";
    public const string Maintenance = "MAINTENANCE";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Maintenance, StringComparison.OrdinalIgnoreCase) ? Maintenance : Available;
}

/// <summary>Estado de validación de un pase de invitado (QR).</summary>
public static class GuestPassStatus
{
    public const string Valid = "VALID";

    /// <summary>Antes de la ventana de ingreso: el día de la reserva desde 1 h antes del inicio (hora del condominio).</summary>
    public const string NotYetValid = "NOT_YET_VALID";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
    public const string Used = "USED";
}
