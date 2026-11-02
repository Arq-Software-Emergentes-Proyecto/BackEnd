namespace BuildingFex.Api.Team.Domain.Model;

public enum TeamError
{
    WorkerFieldsRequired,
    WorkerNotFound,
    WorkerInactive,
    InvalidPosition,
    WorkloadExceeded,
    TaskFieldsRequired,
    TaskNotFound,
    InvalidTaskTransition,
    IncidentNotFound,
    ShiftNotFound,
    ShiftInvalidRange,
    ShiftOverlap,
}

/// <summary>Violación de una invariante del dominio Team (la capa de aplicación la traduce a Result).</summary>
public sealed class TeamDomainException(TeamError error, string message) : InvalidOperationException(message)
{
    public TeamError Error { get; } = error;
}
