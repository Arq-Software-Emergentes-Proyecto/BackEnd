namespace BuildingFex.Api.Communications.Interfaces.Rest.Resources;

/// <summary>
/// status: published | archived | expired (derivado de expiresAt). isActive = vigente ahora.
/// priorityRank: 3 urgent, 2 important, 1 normal (orden del carrusel, RF020).
/// </summary>
public record AnnouncementResource(
    string Id,
    string Title,
    string Body,
    string Priority,
    int PriorityRank,
    int Duration,
    string AuthorId,
    string AuthorName,
    string CreatedAt,
    string PublishedAt,
    string ExpiresAt,
    string Status,
    bool IsActive,
    string OwnerAdminId);

/// <summary>createdAt / expiresAt se aceptan por compatibilidad pero se ignoran: los fija el servidor.</summary>
public record CreateAnnouncementCompatResource(
    string? Id,
    string? Title,
    string? Body,
    string? Priority,
    int Duration,
    string? AuthorId,
    string? AuthorName,
    string? CreatedAt,
    string? ExpiresAt,
    string? OwnerAdminId);

public record UpdateAnnouncementCompatResource(
    string? Title,
    string? Body,
    string? Priority,
    int Duration);
