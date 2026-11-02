using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model.Entities;
using BuildingFex.Api.Incidents.Interfaces.Rest.Resources;

namespace BuildingFex.Api.Incidents.Interfaces.Rest.Transform;

public static class IncidentResourceAssembler
{
    private const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static IncidentResource ToResource(Incident incident) => ToResource(incident, null, false);

    public static IncidentResource ToResource(
        Incident incident,
        BuildingServiceProvider? provider,
        bool includeEvents)
    {
        // Proveedor eliminado: se conserva el nombre guardado en la incidencia.
        return new IncidentResource(
            incident.ExternalId,
            string.IsNullOrWhiteSpace(incident.Title) ? FallbackTitle(incident.Description) : incident.Title,
            incident.ResidentExternalId,
            incident.ResidentName,
            incident.Description,
            incident.Status,
            Iso(incident.ReportedAt)!,
            provider?.Name ?? incident.Provider ?? string.Empty,
            incident.AssigneeId,
            provider?.Phone,
            provider?.Specialty,
            incident.Category,
            incident.Priority,
            incident.ResolutionComment,
            Iso(incident.ResolvedAt),
            Iso(incident.UpdatedAt),
            incident.OwnerAdmin?.ExternalId ?? string.Empty,
            includeEvents
                ? incident.Events.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Select(ToResource).ToList()
                : null);
    }

    public static IncidentEventResource ToResource(IncidentEvent e) =>
        new(e.Id, e.Type, e.Status, e.Comment, e.AuthorId, e.AuthorName, e.AuthorRole, Iso(e.CreatedAt)!);

    /// <summary>Incidencias anteriores a RF024 no tienen asunto: se deriva de la descripción.</summary>
    private static string FallbackTitle(string description)
    {
        var line = (description ?? string.Empty).Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..80].TrimEnd() + "…" : line;
    }

    private static string? Iso(DateTimeOffset? value) => value?.UtcDateTime.ToString(IsoFormat);
}
