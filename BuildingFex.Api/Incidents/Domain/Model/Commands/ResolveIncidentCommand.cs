using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Incidents.Domain.Model.Commands;

/// <summary>PATCH /incidents/{id}/resolve — exige comentario de resolución.</summary>
public record ResolveIncidentCommand(string ExternalId, string? ResolutionComment, IncidentActor Actor);
