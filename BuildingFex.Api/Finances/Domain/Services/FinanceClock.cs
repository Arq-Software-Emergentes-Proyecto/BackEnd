namespace BuildingFex.Api.Finances.Domain.Services;

/// <summary>
/// Fecha contable del condominio: los vencimientos se interpretan en hora de Lima (UTC−5, sin horario de verano),
/// para que el corte de mes y el "vence hoy" no dependan del huso del servidor.
/// </summary>
public static class FinanceClock
{
    private static readonly TimeZoneInfo Lima = ResolveLima();

    private static TimeZoneInfo ResolveLima()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Lima"); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("SA Pacific Standard Time"); }
            catch { return TimeZoneInfo.CreateCustomTimeZone("PET", TimeSpan.FromHours(-5), "PET", "PET"); }
        }
    }

    public static DateOnly Today(DateTimeOffset? utcNow = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow ?? DateTimeOffset.UtcNow, Lima).DateTime);

    public static DateOnly ToLocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Lima).DateTime);

    /// <summary>Primer día del mes en curso (hora Lima).</summary>
    public static DateOnly MonthStart(DateTimeOffset? utcNow = null)
    {
        var today = Today(utcNow);
        return new DateOnly(today.Year, today.Month, 1);
    }

    /// <summary>
    /// Fecha contable (Lima) de un paid_at guardado como texto: "yyyy-MM-dd" ya es una fecha local y se toma tal cual
    /// (antes se leía como medianoche UTC y al pasar a Lima caía el día anterior); un instante ISO se convierte a
    /// Lima (sin offset explícito se asume UTC).
    /// </summary>
    public static bool TryParseLocalDate(string? value, out DateOnly date)
    {
        date = default;
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return false;
        if (raw.Length == 10 && DateOnly.TryParseExact(raw, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date))
            return true;
        if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var instant))
            return false;
        date = ToLocalDate(instant);
        return true;
    }
}
