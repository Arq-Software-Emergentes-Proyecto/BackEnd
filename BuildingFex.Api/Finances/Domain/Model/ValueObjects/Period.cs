using System.Globalization;

namespace BuildingFex.Api.Finances.Domain.Model.ValueObjects;

/// <summary>
/// Mes contable de una cuota (Tabla 60). Se serializa como "yyyy-MM" (receipts.billing_month).
/// </summary>
public readonly record struct Period(int Year, int Month) : IComparable<Period>
{
    public static Period FromDate(DateOnly date) => new(date.Year, date.Month);

    public static bool TryParse(string? value, out Period period)
    {
        period = default;
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length < 6) return false;
        var parts = raw.Split('-');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || year < 1900 || month is < 1 or > 12)
            return false;
        period = new Period(year, month);
        return true;
    }

    /// <summary>Normaliza "2026-5", "2026-05-12" → "2026-05"; null si no es un mes válido.</summary>
    public static string? Normalize(string? value) => TryParse(value, out var p) ? p.Key : null;

    public string Key => $"{Year:D4}-{Month:D2}";

    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => new(Year, Month, DateTime.DaysInMonth(Year, Month));

    public Period Next() => AddMonths(1);
    public Period Previous() => AddMonths(-1);

    public Period AddMonths(int delta)
    {
        var d = FirstDay.AddMonths(delta);
        return new Period(d.Year, d.Month);
    }

    public int CompareTo(Period other) => (Year * 12 + Month).CompareTo(other.Year * 12 + other.Month);

    public override string ToString() => Key;
}
