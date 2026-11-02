using System.Globalization;

namespace BuildingFex.Api.SocialSpaces.Domain.Model.ValueObjects;

/// <summary>
/// Value Object: fecha + rango horario (hora local del condominio) de una reserva.
/// Se normaliza a "yyyy-MM-dd" y "HH:mm" para que "10:00" y "10:00:00" representen el mismo turno
/// (lo exige el índice único de reservas activas).
/// </summary>
public sealed record TimeSlot
{
    public DateOnly Date { get; }
    public TimeOnly Start { get; }
    public TimeOnly End { get; }

    private TimeSlot(DateOnly date, TimeOnly start, TimeOnly end)
    {
        Date = date;
        Start = start;
        End = end;
    }

    public string DateKey => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string StartKey => Start.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string EndKey => End.ToString("HH:mm", CultureInfo.InvariantCulture);
    public int DurationMinutes => (int)(End - Start).TotalMinutes;

    /// <summary>Inicio/fin como hora local (sin zona) del condominio.</summary>
    public DateTime LocalStart => Date.ToDateTime(Start);
    public DateTime LocalEnd => Date.ToDateTime(End);

    /// <summary>Crea el turno; null si la fecha/horas no son válidas o el inicio no es anterior al fin.</summary>
    public static TimeSlot? TryCreate(string? date, string? startTime, string? endTime)
    {
        if (!TryParseDate(date, out var d) || !TryParseTime(startTime, out var s) || !TryParseTime(endTime, out var e))
            return null;
        return s < e ? new TimeSlot(d, s, e) : null;
    }

    /// <summary>Intervalos semiabiertos [start, end): 10:00–11:00 y 11:00–12:00 no se traslapan.</summary>
    public bool Overlaps(TimeSlot other) =>
        Date == other.Date && Start < other.End && other.Start < End;

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact((value ?? string.Empty).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    private static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;
        var parts = (value ?? string.Empty).Trim().Split(':');
        if (parts.Length < 2 || parts.Length > 3)
            return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            return false;
        if (h is < 0 or > 23 || m is < 0 or > 59)
            return false;
        time = new TimeOnly(h, m);
        return true;
    }
}
