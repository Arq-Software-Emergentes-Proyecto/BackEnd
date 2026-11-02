using BuildingFex.Api.Communications.Domain.Model;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Interfaces.Rest.Resources;

namespace BuildingFex.Api.Communications.Interfaces.Rest.Transform;

public static class AnnouncementResourceAssembler
{
    private const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static AnnouncementResource ToResource(Announcement announcement) =>
        ToResource(announcement, DateTimeOffset.UtcNow);

    public static AnnouncementResource ToResource(Announcement announcement, DateTimeOffset now) =>
        new(
            announcement.ExternalId,
            announcement.Title,
            announcement.Body,
            announcement.Priority,
            AnnouncementPriorities.Rank(announcement.Priority),
            announcement.Duration,
            announcement.OwnerAdmin?.ExternalId ?? string.Empty,
            announcement.AuthorName,
            Iso(announcement.CreatedAt),
            Iso(announcement.PublishedAt ?? announcement.CreatedAt),
            Iso(announcement.ExpiresAt),
            announcement.EffectiveStatus(now),
            announcement.IsActive(now),
            announcement.OwnerAdmin?.ExternalId ?? string.Empty);

    private static string Iso(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString(IsoFormat) ?? string.Empty;
}
