namespace BuildingFex.Api.Incidents.Domain.Model.ValueObjects;

/// <summary>Quién ejecuta una acción sobre la incidencia (para el historial).</summary>
public sealed record IncidentActor(string? Id, string? Name, string? Role)
{
    public static readonly IncidentActor System = new(null, null, "system");

    /// <summary>Triaje con IA: solo sugiere categoría y prioridad.</summary>
    public static readonly IncidentActor Assistant = new(null, "Asistente IA", "assistant");
}
