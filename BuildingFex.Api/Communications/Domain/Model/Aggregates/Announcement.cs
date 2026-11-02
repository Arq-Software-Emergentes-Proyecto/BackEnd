using BuildingFex.Api.Communications.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Communications.Domain.Model.Aggregates;

/// <summary>
/// Comunicado oficial. La vigencia la calcula el dominio: ExpiresAt = CreatedAt + Duration (días),
/// tanto al crear como al editar (RF019 / RNF011). El cliente no puede fijar ExpiresAt.
/// </summary>
public class Announcement : IAuditableEntity
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public User? OwnerAdmin { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string Priority { get; private set; } = AnnouncementPriorities.Normal;

    /// <summary>Días de vigencia (7 | 15 | 30 en el FE).</summary>
    public int Duration { get; private set; }
    public string AuthorName { get; private set; } = string.Empty;
    public string Status { get; private set; } = AnnouncementStatuses.Published;
    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private Announcement()
    {
    }

    public static Announcement Create(
        string externalId,
        int ownerAdminId,
        string title,
        string body,
        string priority = AnnouncementPriorities.Normal,
        int duration = AnnouncementDurations.DefaultDays,
        string? authorName = null,
        DateTimeOffset? createdAt = null)
    {
        var created = createdAt ?? DateTimeOffset.UtcNow;
        var announcement = new Announcement
        {
            ExternalId = externalId.Trim(),
            OwnerAdminId = ownerAdminId,
            Title = title.Trim(),
            Body = body?.Trim() ?? string.Empty,
            Priority = AnnouncementPriorities.Normalize(priority),
            Duration = NormalizeDuration(duration),
            AuthorName = authorName?.Trim() ?? string.Empty,
            Status = AnnouncementStatuses.Published,
            CreatedAt = created,
            PublishedAt = created,
        };
        announcement.RecalculateExpiration();
        return announcement;
    }

    public void Update(
        string title,
        string body,
        string priority,
        int duration)
    {
        Title = title.Trim();
        Body = body?.Trim() ?? string.Empty;
        Priority = string.IsNullOrWhiteSpace(priority) ? Priority : AnnouncementPriorities.Normalize(priority);
        // duration 0 / ausente en el body = conservar la vigencia actual.
        Duration = duration <= 0 ? NormalizeDuration(Duration) : NormalizeDuration(duration);
        UpdatedAt = DateTimeOffset.UtcNow;
        RecalculateExpiration();
    }

    /// <summary>isActive(): publicado y no vencido.</summary>
    public bool IsActive(DateTimeOffset now) =>
        Status == AnnouncementStatuses.Published && ExpiresAt is not null && ExpiresAt > now;

    /// <summary>Estado para la API: "expired" si ya venció.</summary>
    public string EffectiveStatus(DateTimeOffset now) =>
        Status == AnnouncementStatuses.Published && ExpiresAt is not null && ExpiresAt <= now
            ? AnnouncementStatuses.Expired
            : Status;

    public static bool IsValidDuration(int duration) => duration >= 0 && duration <= AnnouncementDurations.MaxDays;

    private void RecalculateExpiration()
    {
        var start = PublishedAt ?? CreatedAt ?? DateTimeOffset.UtcNow;
        ExpiresAt = start.AddDays(Duration);
    }

    private static int NormalizeDuration(int duration) =>
        duration <= 0 ? AnnouncementDurations.DefaultDays : Math.Min(duration, AnnouncementDurations.MaxDays);
}
