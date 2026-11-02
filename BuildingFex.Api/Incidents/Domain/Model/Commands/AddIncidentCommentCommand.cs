using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Incidents.Domain.Model.Commands;

/// <summary>POST /incidents/{id}/comments — comentario de seguimiento.</summary>
public record AddIncidentCommentCommand(string ExternalId, string? Comment, IncidentActor Actor);
