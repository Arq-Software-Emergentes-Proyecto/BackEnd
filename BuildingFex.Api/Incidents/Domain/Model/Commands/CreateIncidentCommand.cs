using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Incidents.Domain.Model.Commands;

public record CreateIncidentCommand(
    string ExternalId,
    string OwnerAdminExternalId,
    string Description,
    string Status,
    string? ResidentExternalId,
    string? ResidentName,
    string? Provider,
    string? CreatedAt,
    string? Title = null,
    string? Category = null,
    string? Priority = null,
    string? ProviderExternalId = null,
    IncidentActor? Actor = null,
    string? SourceChatId = null);
