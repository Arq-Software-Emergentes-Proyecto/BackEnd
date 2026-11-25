using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Repositories;
using BuildingFex.Api.ResidentCare.Application;
using BuildingFex.Api.ResidentCare.Infrastructure.PlanQuota;
using BuildingFex.Api.Shared.Domain.Repositories;

namespace BuildingFex.Api.Incidents.Application.Internal.CommandServices;

public enum TriageStatus
{
    Suggested,
    NotFound,
    PlanFeatureNotIncluded,
    MonthlyCapReached,
    Unavailable,
}

public sealed record TriageOutcome(TriageStatus Status, Incident? Incident = null, string? Code = null);

/// <summary>
/// Triaje con IA (módulo aiIncidentClassification, planes Standard en adelante): pide a la IA categoría y prioridad
/// y las guarda como sugerencia. Cada llamada cuenta para el tope mensual de consultas del edificio (CON11);
/// si la IA falla, la consulta se devuelve.
/// </summary>
public class IncidentTriageService(
    IIncidentRepository incidentRepository,
    IIncidentTriageAdvisor advisor,
    IPlanQuotaClient planQuota,
    IAiUsageQuota aiQuota,
    IUnitOfWork unitOfWork,
    ILogger<IncidentTriageService> logger)
{
    public bool IsAvailableFor(User tenantAdmin) =>
        planQuota.IncludesModule(
            tenantAdmin.SubscriptionPlanId,
            tenantAdmin.SubscriptionPaidUntil,
            PlanModules.AiIncidentClassification);

    /// <param name="timeout">Tope para la llamada a la IA (alta de incidencia: no debe demorar el reporte).</param>
    public async Task<TriageOutcome> SuggestAsync(
        string externalId,
        User tenantAdmin,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        if (!IsAvailableFor(tenantAdmin))
            return new TriageOutcome(TriageStatus.PlanFeatureNotIncluded);

        var incident = await incidentRepository.FindByExternalIdAsync(externalId, ct);
        if (incident is null)
            return new TriageOutcome(TriageStatus.NotFound);

        var cap = planQuota.MonthlyAiQueryCap(tenantAdmin.SubscriptionPlanId, tenantAdmin.SubscriptionPaidUntil);
        // El tope mensual se cuenta por cuenta (en Portafolio, la matriz con todos sus edificios).
        var reservation = await aiQuota.TryConsumeAsync(tenantAdmin.BillingAccount.ExternalId, cap, ct);
        if (reservation is null)
            return new TriageOutcome(TriageStatus.MonthlyCapReached, incident);

        TriageSuggestion suggestion;
        using var aiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) aiCts.CancelAfter(timeout.Value);
        try
        {
            suggestion = await advisor.SuggestAsync(incident.Title, incident.Description, aiCts.Token);
        }
        catch (Exception ex) when (ex is TriageUnavailableException or OperationCanceledException or HttpRequestException)
        {
            await aiQuota.ReleaseAsync(reservation, CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            var code = (ex as TriageUnavailableException)?.Code ?? "AI_TRIAGE_TIMEOUT";
            logger.LogWarning(ex, "Incident triage failed ({Code}) for {Incident}", code, incident.ExternalId);
            return new TriageOutcome(TriageStatus.Unavailable, incident, code);
        }

        incident.RecordTriageSuggestion(suggestion.Category, suggestion.Priority, suggestion.Reason, DateTimeOffset.UtcNow);
        await unitOfWork.CompleteAsync(ct);

        var updated = await incidentRepository.FindByExternalIdAsync(incident.ExternalId, ct);
        return new TriageOutcome(TriageStatus.Suggested, updated);
    }
}
