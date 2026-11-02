namespace BuildingFex.Api.Team.Domain.Model.ValueObjects;

/// <summary>Puesto del trabajador (Tabla 80 – Position).</summary>
public static class StaffPosition
{
    public const string Concierge = "CONCIERGE";
    public const string Maintenance = "MAINTENANCE";
    public const string Cleaning = "CLEANING";
    public const string Security = "SECURITY";

    /// <summary>Puesto por defecto de las filas anteriores a este campo.</summary>
    public const string Default = Maintenance;

    public static readonly IReadOnlyList<string> All = [Concierge, Maintenance, Cleaning, Security];

    /// <summary>Normaliza (mayúsculas, sin espacios). Null si no es un puesto válido.</summary>
    public static string? Normalize(string? value)
    {
        var clean = (value ?? string.Empty).Trim().ToUpperInvariant();
        return All.Contains(clean) ? clean : null;
    }
}
