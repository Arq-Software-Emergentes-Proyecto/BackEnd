namespace BuildingFex.Api.Communications.Domain.Model;

/// <summary>Prioridades usadas por el FE (normal | important | urgent). Rank: mayor = más arriba en el carrusel (RF020).</summary>
public static class AnnouncementPriorities
{
    public const string Normal = "normal";
    public const string Important = "important";
    public const string Urgent = "urgent";

    public static string Normalize(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return Normal;
        return priority.Trim().ToLowerInvariant() switch
        {
            "urgent" or "critical" or "urgente" => Urgent,
            "important" or "high" or "importante" => Important,
            _ => Normal,
        };
    }

    public static int Rank(string? priority) => Normalize(priority) switch
    {
        Urgent => 3,
        Important => 2,
        _ => 1,
    };
}

/// <summary>
/// Estado del comunicado (diseño táctico: draft, published, archived). "expired" no se persiste:
/// se deriva de ExpiresAt (RNF011 — nunca se muestra vencido como vigente).
/// </summary>
public static class AnnouncementStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";
    public const string Expired = "expired";
}

public static class AnnouncementDurations
{
    public const int DefaultDays = 7;
    public const int MaxDays = 365;
}
