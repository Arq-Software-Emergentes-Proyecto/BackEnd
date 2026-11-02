using BuildingFex.Api.Incidents.Domain.Model.Aggregates;

namespace BuildingFex.Api.Incidents.Application.Internal.OutboundServices;

/// <summary>IncidenteResuelto → Resident Care: avisa en la conversación desde la que se reportó.</summary>
public interface IIncidentResolutionNotifier
{
    Task NotifyResolvedAsync(Incident incident, CancellationToken ct = default);
}
