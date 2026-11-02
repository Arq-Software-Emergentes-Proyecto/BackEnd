namespace BuildingFex.Api.Incidents.Domain.Model;

public enum IncidentError
{
    IncidentNotFound,
    OwnerAdminRequired,
    DescriptionRequired,
    InvalidStatus,
    ResidentRequired,
    ProviderRequired,
    TitleRequired,
    ResolutionCommentRequired,
    CommentRequired,
    ProviderNotFound,
}

/// <summary>Violación de una regla de negocio del agregado Incident.</summary>
public class IncidentDomainException(IncidentError error, string message) : Exception(message)
{
    public IncidentError Error { get; } = error;
}
