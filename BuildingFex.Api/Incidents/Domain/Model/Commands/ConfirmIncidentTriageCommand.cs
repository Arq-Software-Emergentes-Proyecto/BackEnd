using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Incidents.Domain.Model.Commands;

/// <summary>PATCH /incidents/{id}/triage. Sin categoría/prioridad se aplica la sugerencia de la IA.</summary>
public record ConfirmIncidentTriageCommand(
    string ExternalId,
    string? Category,
    string? Priority,
    IncidentActor Actor);
