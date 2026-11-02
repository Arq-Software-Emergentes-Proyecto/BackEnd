using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Incidents.Domain.Model.Commands;

/// <summary>PUT compat: edita detalles, proveedor y estado (resuelto exige ResolutionComment).</summary>
public record UpdateIncidentCommand(
    string ExternalId,
    string Description,
    string Status,
    string? Provider,
    string? ResidentExternalId,
    string? ResidentName,
    string? Title = null,
    string? Category = null,
    string? Priority = null,
    string? ProviderExternalId = null,
    string? ResolutionComment = null,
    IncidentActor? Actor = null);
