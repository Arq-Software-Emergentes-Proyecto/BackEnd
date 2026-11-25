using BuildingFex.Api.Incidents.Domain.Model.Aggregates;

namespace BuildingFex.Api.Incidents.Domain.Model;

/// <summary>
/// Regla de escalamiento: una incidencia reportada que sigue sin proveedor asignado después de
/// <see cref="Threshold"/> se escala y se avisa a la administración (junta directiva o empresa administradora).
/// </summary>
public static class EscalationPolicy
{
    public static readonly TimeSpan Threshold = TimeSpan.FromHours(48);

    public static bool MustEscalate(Incident incident, DateTimeOffset now) =>
        incident.Status == IncidentStatuses.Open
        && incident.IsUnassigned
        && incident.EscalatedAt is null
        && incident.ReportedAt <= now - Threshold;
}
