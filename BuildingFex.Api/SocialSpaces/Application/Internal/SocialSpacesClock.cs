namespace BuildingFex.Api.SocialSpaces.Application.Internal;

/// <summary>
/// Hora actual y zona horaria del condominio. Las reservas guardan fecha/hora local ("2026-08-01", "20:00");
/// para calcular el vencimiento de los pases QR se interpretan en <c>SocialSpaces:TimeZone</c> (default America/Lima).
/// </summary>
public class SocialSpacesClock(IConfiguration configuration, ILogger<SocialSpacesClock> logger)
{
    private TimeZoneInfo? _timeZone;

    public virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeZoneInfo BuildingTimeZone => _timeZone ??= ResolveTimeZone();

    private TimeZoneInfo ResolveTimeZone()
    {
        var id = configuration["SocialSpaces:TimeZone"];
        if (string.IsNullOrWhiteSpace(id)) id = "America/Lima";
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Contenedores sin tzdata: Lima es UTC-5 sin horario de verano.
            logger.LogWarning("SocialSpaces: time zone {Id} not found; using fixed UTC-05:00", id);
            return TimeZoneInfo.CreateCustomTimeZone("BuildingFex/UTC-5", TimeSpan.FromHours(-5), "UTC-05:00", "UTC-05:00");
        }
    }
}
