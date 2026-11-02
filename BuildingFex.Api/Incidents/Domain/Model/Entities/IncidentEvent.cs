using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Incidents.Domain.Model.Entities;

/// <summary>
/// Evento del historial de una incidencia (cambio de estado, asignación, resolución, comentario de seguimiento).
/// Se crea solo desde el agregado <see cref="Aggregates.Incident"/>.
/// </summary>
public class IncidentEvent
{
    public int Id { get; private set; }
    public int IncidentId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string? Status { get; private set; }
    public string? Comment { get; private set; }
    public string? AuthorId { get; private set; }
    public string? AuthorName { get; private set; }
    public string? AuthorRole { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private IncidentEvent()
    {
    }

    internal static IncidentEvent Create(
        string type,
        string? status,
        string? comment,
        IncidentActor? actor,
        DateTimeOffset? at = null) =>
        new()
        {
            Type = type,
            Status = status,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            AuthorId = actor?.Id,
            AuthorName = string.IsNullOrWhiteSpace(actor?.Name) ? null : actor!.Name!.Trim(),
            AuthorRole = actor?.Role,
            CreatedAt = at ?? DateTimeOffset.UtcNow,
        };
}
