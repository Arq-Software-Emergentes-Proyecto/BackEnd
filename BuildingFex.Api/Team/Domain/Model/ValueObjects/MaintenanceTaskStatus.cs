namespace BuildingFex.Api.Team.Domain.Model.ValueObjects;

/// <summary>Estado de la tarea (Tabla 80 – TaskStatus): PENDING → IN_PROGRESS → COMPLETED.</summary>
public static class MaintenanceTaskStatus
{
    public const string Pending = "PENDING";
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";

    public static readonly IReadOnlyList<string> All = [Pending, InProgress, Completed];

    public static string? Normalize(string? value)
    {
        var clean = (value ?? string.Empty).Trim().ToUpperInvariant().Replace('-', '_');
        return All.Contains(clean) ? clean : null;
    }

    public static bool IsOpen(string status) => status is Pending or InProgress;
}
