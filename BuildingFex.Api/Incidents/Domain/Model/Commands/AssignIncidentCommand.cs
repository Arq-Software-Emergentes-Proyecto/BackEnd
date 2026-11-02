using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Incidents.Domain.Model.Commands;

/// <summary>PATCH /incidents/{id}/assign. ProviderExternalId vacío = desasignar.</summary>
public record AssignIncidentCommand(
    string ExternalId,
    string? ProviderExternalId,
    string? ProviderName,
    string? Status,
    IncidentActor Actor);
