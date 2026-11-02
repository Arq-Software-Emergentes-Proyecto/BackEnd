namespace BuildingFex.Api.Communications.Domain.Model.Commands;

public record CreateAnnouncementCommand(
    string ExternalId,
    string OwnerAdminExternalId,
    string Title,
    string Body,
    string Priority,
    int Duration,
    string? AuthorName,
    string? CreatedAt,
    string? ExpiresAt);
