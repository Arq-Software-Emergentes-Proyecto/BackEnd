using BuildingFex.Api.Incidents.Application.Internal.OutboundServices;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;
using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.ResidentCare.Application;

/// <summary>
/// Consume IncidenteResuelto: deja en el chat del residente (ya cerrado al registrar la incidencia)
/// el comentario de resolución.
/// </summary>
public class IncidentResolutionNotifier(AppDbContext db, ILogger<IncidentResolutionNotifier> logger)
    : IIncidentResolutionNotifier
{
    public async Task NotifyResolvedAsync(Incident incident, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(incident.SourceChatId)) return;

        var chat = await db.Set<SupportChat>()
            .FirstOrDefaultAsync(c => c.ExternalId == incident.SourceChatId, ct);
        if (chat is null)
        {
            logger.LogInformation("Incident {Incident} resolved; source chat {Chat} no longer exists", incident.ExternalId, incident.SourceChatId);
            return;
        }

        var title = string.IsNullOrWhiteSpace(incident.Title) ? "tu incidencia" : $"«{incident.Title}»";
        var detail = string.IsNullOrWhiteSpace(incident.ResolutionComment) ? string.Empty : $" Detalle: {incident.ResolutionComment}";
        var body = $"Tu incidencia {title} fue resuelta.{detail} Si el problema continúa, inicia una nueva consulta.";
        if (chat.Kind == SupportChat.KindAdmin)
            chat.AddMessage(Sender.Admin, body, "Administración");
        else
            chat.AddMessage(Sender.Assistant, body);
        chat.Close();
        await db.SaveChangesAsync(ct);
    }
}
