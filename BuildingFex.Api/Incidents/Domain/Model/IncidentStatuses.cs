namespace BuildingFex.Api.Incidents.Domain.Model;

/// <summary>
/// Estados persistidos de una incidencia. Se conservan los strings históricos (open / in-progress / resolved)
/// para no romper datos existentes. Equivalencia con el diseño táctico (IncidentStatus):
/// open = REPORTED, in-progress = ASSIGNED / IN_PROGRESS, resolved = RESOLVED.
/// </summary>
public static class IncidentStatuses
{
    public const string Open = "open";
    public const string InProgress = "in-progress";
    public const string Resolved = "resolved";

    public static readonly IReadOnlyList<string> All = [Open, InProgress, Resolved];

    public static string Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Open;

        return status.Trim().ToLowerInvariant() switch
        {
            "open" or "reported" => Open,
            "in-progress" or "in_progress" or "inprogress" or "assigned" => InProgress,
            "resolved" or "closed" => Resolved,
            _ => status.Trim(),
        };
    }

    public static bool IsValid(string? status)
    {
        var normalized = Normalize(status);
        return normalized is Open or InProgress or Resolved;
    }

    /// <summary>Nombre del diseño táctico (documentación / reportes).</summary>
    public static string ToReportName(string status) => Normalize(status) switch
    {
        Open => "REPORTED",
        InProgress => "IN_PROGRESS",
        Resolved => "RESOLVED",
        _ => status.ToUpperInvariant(),
    };
}

/// <summary>Prioridad (diseño táctico: LOW, MEDIUM, HIGH, CRITICAL). Opcional.</summary>
public static class IncidentPriorities
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";

    public static string? Normalize(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return null;
        var p = priority.Trim().ToLowerInvariant();
        return p is Low or Medium or High or Critical ? p : null;
    }
}

/// <summary>Tipos de evento del historial de una incidencia (RNF010 trazabilidad).</summary>
public static class IncidentEventTypes
{
    public const string Reported = "reported";
    public const string StatusChanged = "status_changed";
    public const string Assigned = "assigned";
    public const string Unassigned = "unassigned";
    public const string Resolved = "resolved";
    public const string Comment = "comment";
    public const string Escalated = "escalated";
    public const string AiTriageSuggested = "ai_triage_suggested";
    public const string TriageConfirmed = "triage_confirmed";
}

/// <summary>
/// Categorías que la IA puede sugerir. La categoría se guarda como texto libre, así que el administrador
/// también puede escribir otra.
/// </summary>
public static class IncidentCategories
{
    public static readonly IReadOnlyList<string> Suggested =
    [
        "Gasfitería", "Electricidad", "Ascensores", "Seguridad", "Limpieza",
        "Áreas comunes", "Estructura", "Convivencia", "Otros",
    ];

    /// <summary>Devuelve la categoría de la lista que coincide (sin tildes ni mayúsculas) o null.</summary>
    public static string? Match(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = Fold(value);
        return Suggested.FirstOrDefault(c => Fold(c) == key);
    }

    private static string Fold(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(normalized
            .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                         != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
    }
}
