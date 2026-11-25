using System.Text.Json;
using BuildingFex.Api.Incidents.Application.Internal.CommandServices;
using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.ResidentCare.Infrastructure.Gemini;

namespace BuildingFex.Api.Incidents.Infrastructure.Triage;

/// <summary>
/// Adaptador (ACL) que pide a Gemini la categoría y prioridad de una incidencia. La respuesta se fuerza a JSON
/// y se valida contra <see cref="IncidentCategories"/> e <see cref="IncidentPriorities"/>: nada fuera de la lista
/// llega al agregado.
/// </summary>
public class GeminiIncidentTriageAdvisor(IGeminiClient gemini, ILogger<GeminiIncidentTriageAdvisor> logger)
    : IIncidentTriageAdvisor
{
    private static readonly string SystemInstruction = $$"""
        Eres el asistente de triaje de incidencias de un condominio en Perú.
        Clasifica la incidencia que reporta un residente.
        Responde SOLO con un objeto JSON, sin texto adicional ni bloques de código:
        {"category": "<una de: {{string.Join(", ", IncidentCategories.Suggested)}}>",
         "priority": "<low | medium | high | critical>",
         "reason": "<una frase corta en español que explique la prioridad>"}
        Criterios de prioridad:
        - critical: riesgo para personas o del edificio (fuga de gas, incendio, cortocircuito, persona atrapada en el ascensor, inundación).
        - high: afecta un servicio esencial a varios departamentos (sin agua, sin luz en áreas comunes, ascensor fuera de servicio).
        - medium: afecta a un departamento o un área común sin urgencia (filtración menor, luminaria quemada).
        - low: molestias o temas estéticos y de convivencia sin daño.
        Si no hay datos suficientes, usa "Otros" y "medium".
        """;

    public async Task<TriageSuggestion> SuggestAsync(string title, string description, CancellationToken ct = default)
    {
        var prompt = $"Asunto: {title}\nDescripción: {description}";
        string reply;
        try
        {
            reply = await gemini.GenerateReplyAsync([new GeminiTurn("user", prompt)], SystemInstruction, ct);
        }
        catch (GeminiException ex)
        {
            throw new TriageUnavailableException(ex.Code, ex.Message, ex);
        }

        var suggestion = Parse(reply);
        if (suggestion is null)
        {
            logger.LogWarning("Incident triage: unexpected AI reply ({Length} chars)", reply?.Length ?? 0);
            throw new TriageUnavailableException("AI_TRIAGE_INVALID_REPLY", "La IA no devolvió una clasificación válida.");
        }

        return suggestion;
    }

    /// <summary>Extrae el primer objeto JSON de la respuesta y lo normaliza. Null si no es utilizable.</summary>
    internal static TriageSuggestion? Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = doc.RootElement;
            var category = IncidentCategories.Match(Read(root, "category"));
            var priority = IncidentPriorities.Normalize(Read(root, "priority"));
            if (category is null && priority is null) return null;
            return new TriageSuggestion(category, priority, Read(root, "reason")?.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Read(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
