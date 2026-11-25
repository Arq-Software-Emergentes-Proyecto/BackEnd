namespace BuildingFex.Api.Incidents.Application.Internal.CommandServices;

/// <summary>Categoría y prioridad propuestas para una incidencia, con una breve justificación.</summary>
public sealed record TriageSuggestion(string? Category, string? Priority, string? Reason);

/// <summary>Puerto hacia el modelo de IA que clasifica incidencias (solo sugiere; confirma el administrador).</summary>
public interface IIncidentTriageAdvisor
{
    /// <summary>Lanza <see cref="TriageUnavailableException"/> si la IA no responde o la respuesta no sirve.</summary>
    Task<TriageSuggestion> SuggestAsync(string title, string description, CancellationToken ct = default);
}

public class TriageUnavailableException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}
