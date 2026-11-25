using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Repositories;
using BuildingFex.Api.Incidents.Interfaces.Rest.Transform;
using BuildingFex.Api.ResidentCare.Interfaces.Hubs;
using BuildingFex.Api.Shared.Domain.Repositories;
using Microsoft.AspNetCore.SignalR;

namespace BuildingFex.Api.Incidents.Application.Internal.CommandServices;

/// <summary>
/// Escalamiento automático: marca las incidencias que siguen abiertas y sin proveedor después de
/// <see cref="EscalationPolicy.Threshold"/> y avisa al administrador del condominio (evento en el historial,
/// alerta en el panel y "incidentUpdated" por SignalR cuando hay conexión). Idempotente: cada incidencia
/// se escala una sola vez. En AWS corre como job "incident-escalation" cada hora.
/// </summary>
public class IncidentEscalationJob(
    IIncidentRepository incidentRepository,
    IUnitOfWork unitOfWork,
    IHubContext<SupportHub> hub,
    ILogger<IncidentEscalationJob> logger)
{
    private const int BatchSize = 200;

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await incidentRepository.ListEscalationCandidatesAsync(
            now - EscalationPolicy.Threshold, BatchSize, ct);

        var escalated = candidates.Where(i => i.Escalate(now)).ToList();
        if (escalated.Count == 0) return 0;

        await unitOfWork.CompleteAsync(ct);
        logger.LogInformation("Incident escalation: {Count} incident(s) escalated", escalated.Count);

        foreach (var incident in escalated)
        {
            var owner = incident.OwnerAdmin?.ExternalId;
            if (string.IsNullOrWhiteSpace(owner)) continue;
            try
            {
                var body = IncidentResourceAssembler.ToResource(incident, null, includeEvents: false);
                await hub.Clients.Group(SupportHub.OwnerGroup(owner)).SendAsync("incidentUpdated", body, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Escalation notification failed for {Incident}", incident.ExternalId);
            }
        }

        return escalated.Count;
    }
}
