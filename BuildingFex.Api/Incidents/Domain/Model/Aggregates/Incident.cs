using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Entities;
using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;
using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Incidents.Domain.Model.Aggregates;

/// <summary>
/// Incidencia reportada por un residente (o registrada por el admin).
/// Ciclo de vida: open → in-progress → resolved. Pasar a resuelta exige comentario de resolución (RF031).
/// Cada cambio queda en <see cref="Events"/> (RNF010 trazabilidad).
/// </summary>
public class Incident : IAuditableEntity
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int CommentMaxLength = 2000;
    public const int CategoryMaxLength = 50;
    public const int TriageReasonMaxLength = 300;

    private readonly List<IncidentEvent> _events = [];

    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public User? OwnerAdmin { get; private set; }
    public string? ResidentExternalId { get; private set; }
    public string ResidentName { get; private set; } = string.Empty;

    /// <summary>Asunto (RF024).</summary>
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string? Category { get; private set; }
    public string? Priority { get; private set; }
    public string Status { get; private set; } = IncidentStatuses.Open;

    /// <summary>Nombre del proveedor asignado (compat: antes era el único dato del proveedor).</summary>
    public string? Provider { get; private set; }

    /// <summary>assigneeId: external id del proveedor (service_providers.external_id).</summary>
    public string? AssigneeId { get; private set; }

    public string? ResolutionComment { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>Sugerencia de triaje de la IA (la confirma el administrador; no cambia Category/Priority).</summary>
    public string? AiSuggestedCategory { get; private set; }
    public string? AiSuggestedPriority { get; private set; }
    public string? AiSuggestionReason { get; private set; }
    public DateTimeOffset? AiSuggestedAt { get; private set; }

    /// <summary>Conversación de Resident Care desde la que se reportó (ReportarIncidente); recibe el aviso de resolución.</summary>
    public string? SourceChatId { get; private set; }
    public DateTimeOffset ReportedAt { get; private set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public IReadOnlyCollection<IncidentEvent> Events => _events;

    public bool HasPendingTriageSuggestion =>
        AiSuggestedAt is not null
        && (!string.Equals(AiSuggestedCategory, Category, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(AiSuggestedPriority, Priority, StringComparison.Ordinal));

    private Incident()
    {
    }

    public static Incident Create(
        string externalId,
        int ownerAdminId,
        string description,
        string status = IncidentStatuses.Open,
        string? residentExternalId = null,
        string? residentName = null,
        string? provider = null,
        DateTimeOffset? reportedAt = null,
        string? title = null,
        string? category = null,
        string? priority = null,
        string? assigneeId = null,
        IncidentActor? reporter = null)
    {
        var normalizedStatus = IncidentStatuses.Normalize(status);
        if (!IncidentStatuses.IsValid(normalizedStatus))
            throw new IncidentDomainException(IncidentError.InvalidStatus, "Estado inválido.");

        var now = DateTimeOffset.UtcNow;
        var incident = new Incident
        {
            ExternalId = externalId.Trim(),
            OwnerAdminId = ownerAdminId,
            Title = Clip(title, TitleMaxLength),
            Description = Clip(description, DescriptionMaxLength),
            Category = string.IsNullOrWhiteSpace(category) ? null : Clip(category, 50),
            Priority = IncidentPriorities.Normalize(priority),
            Status = normalizedStatus,
            ResidentExternalId = string.IsNullOrWhiteSpace(residentExternalId) ? null : residentExternalId.Trim(),
            ResidentName = residentName?.Trim() ?? string.Empty,
            Provider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim(),
            AssigneeId = string.IsNullOrWhiteSpace(assigneeId) ? null : assigneeId.Trim(),
            ReportedAt = reportedAt ?? now,
            CreatedAt = now,
        };

        if (normalizedStatus == IncidentStatuses.Resolved)
            incident.ResolvedAt = incident.ReportedAt;

        incident._events.Add(IncidentEvent.Create(
            IncidentEventTypes.Reported,
            normalizedStatus,
            null,
            reporter ?? new IncidentActor(incident.ResidentExternalId, incident.ResidentName, "resident"),
            incident.ReportedAt));

        return incident;
    }

    /// <summary>Edita asunto/descripción/clasificación (no cambia estado ni asignación).</summary>
    public void UpdateDetails(
        string? title,
        string description,
        string? category = null,
        string? priority = null,
        string? residentExternalId = null,
        string? residentName = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new IncidentDomainException(IncidentError.DescriptionRequired, "La descripción es obligatoria.");

        Description = Clip(description, DescriptionMaxLength);
        if (title is not null && !string.IsNullOrWhiteSpace(title))
            Title = Clip(title, TitleMaxLength);
        if (category is not null)
            Category = string.IsNullOrWhiteSpace(category) ? null : Clip(category, 50);
        if (priority is not null)
            Priority = IncidentPriorities.Normalize(priority);

        if (residentExternalId is not null)
            ResidentExternalId = string.IsNullOrWhiteSpace(residentExternalId) ? null : residentExternalId.Trim();

        if (residentName is not null)
            ResidentName = residentName.Trim();

        Touch();
    }

    /// <summary>
    /// assign(): asigna (o desasigna con id/nombre vacíos) un proveedor.
    /// Con <paramref name="advanceStatus"/> una incidencia abierta pasa a "en proceso".
    /// </summary>
    public void Assign(string? assigneeId, string? providerName, IncidentActor actor, bool advanceStatus = true)
    {
        var id = string.IsNullOrWhiteSpace(assigneeId) ? null : assigneeId.Trim();
        var name = string.IsNullOrWhiteSpace(providerName) ? null : providerName.Trim();

        if (id is null && name is null)
        {
            if (AssigneeId is null && Provider is null) return;
            AssigneeId = null;
            Provider = null;
            _events.Add(IncidentEvent.Create(IncidentEventTypes.Unassigned, Status, null, actor));
            Touch();
            return;
        }

        var sameProvider = id is null
            ? string.Equals(name, Provider, StringComparison.Ordinal)
            : string.Equals(id, AssigneeId, StringComparison.Ordinal);
        if (sameProvider)
        {
            if (id is not null && !string.Equals(name, Provider, StringComparison.Ordinal))
                Provider = name; // el proveedor cambió de nombre: solo se sincroniza
            return;
        }

        AssigneeId = id;
        Provider = name;
        if (advanceStatus && Status == IncidentStatuses.Open)
            Status = IncidentStatuses.InProgress;

        _events.Add(IncidentEvent.Create(IncidentEventTypes.Assigned, Status, name, actor));
        Touch();
    }

    /// <summary>Cambio de estado. Pasar a resuelta delega en <see cref="Resolve"/> (exige comentario).</summary>
    public void ChangeStatus(string status, string? comment, IncidentActor actor)
    {
        var normalized = IncidentStatuses.Normalize(status);
        if (!IncidentStatuses.IsValid(normalized))
            throw new IncidentDomainException(IncidentError.InvalidStatus, "Estado inválido.");

        if (normalized == Status) return;

        if (normalized == IncidentStatuses.Resolved)
        {
            Resolve(comment, actor);
            return;
        }

        if (Status == IncidentStatuses.Resolved)
        {
            // Reapertura: el detalle de cierre anterior queda en el historial.
            ResolvedAt = null;
            ResolutionComment = null;
        }

        Status = normalized;
        _events.Add(IncidentEvent.Create(IncidentEventTypes.StatusChanged, normalized, comment, actor));
        Touch();
    }

    /// <summary>resolve(): requiere comentario de resolución no vacío (RF031 / US031).</summary>
    public void Resolve(string? resolutionComment, IncidentActor actor)
    {
        if (string.IsNullOrWhiteSpace(resolutionComment))
            throw new IncidentDomainException(
                IncidentError.ResolutionCommentRequired,
                "Documenta la solución (comentario de resolución) antes de marcar la incidencia como resuelta.");

        var now = DateTimeOffset.UtcNow;
        Status = IncidentStatuses.Resolved;
        ResolutionComment = Clip(resolutionComment, CommentMaxLength);
        ResolvedAt = now;
        _events.Add(IncidentEvent.Create(IncidentEventTypes.Resolved, Status, ResolutionComment, actor, now));
        Touch();
    }

    /// <summary>Comentario de seguimiento (admin o residente que reportó).</summary>
    public IncidentEvent AddComment(string? comment, IncidentActor actor)
    {
        if (string.IsNullOrWhiteSpace(comment))
            throw new IncidentDomainException(IncidentError.CommentRequired, "El comentario es obligatorio.");

        var ev = IncidentEvent.Create(IncidentEventTypes.Comment, Status, Clip(comment, CommentMaxLength), actor);
        _events.Add(ev);
        Touch();
        return ev;
    }

    public void LinkSourceChat(string? chatExternalId)
    {
        if (string.IsNullOrWhiteSpace(chatExternalId)) return;
        SourceChatId = chatExternalId.Trim();
    }

    /// <summary>Guarda la sugerencia de categoría y prioridad de la IA (la IA solo sugiere).</summary>
    public void RecordTriageSuggestion(string? category, string? priority, string? reason, DateTimeOffset now)
    {
        var cat = string.IsNullOrWhiteSpace(category) ? null : Clip(category, CategoryMaxLength);
        var pri = IncidentPriorities.Normalize(priority);
        if (cat is null && pri is null) return;

        AiSuggestedCategory = cat;
        AiSuggestedPriority = pri;
        AiSuggestionReason = string.IsNullOrWhiteSpace(reason) ? null : Clip(reason, TriageReasonMaxLength);
        AiSuggestedAt = now;
        _events.Add(IncidentEvent.Create(
            IncidentEventTypes.AiTriageSuggested,
            Status,
            TriageSummary(cat, pri),
            IncidentActor.Assistant,
            now));
        Touch();
    }

    /// <summary>
    /// El administrador confirma la clasificación. Sin valores explícitos se toma la sugerencia de la IA.
    /// </summary>
    public void ConfirmTriage(string? category, string? priority, IncidentActor actor)
    {
        var cat = string.IsNullOrWhiteSpace(category) ? AiSuggestedCategory : Clip(category, CategoryMaxLength);
        var pri = IncidentPriorities.Normalize(priority) ?? AiSuggestedPriority;
        if (cat is null && pri is null)
            throw new IncidentDomainException(
                IncidentError.TriageSuggestionRequired,
                "No hay una sugerencia de triaje para confirmar.");

        Category = cat ?? Category;
        Priority = pri ?? Priority;
        _events.Add(IncidentEvent.Create(
            IncidentEventTypes.TriageConfirmed,
            Status,
            TriageSummary(Category, Priority),
            actor));
        Touch();
    }

    private static string TriageSummary(string? category, string? priority)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(category)) parts.Add($"Categoría: {category}");
        if (!string.IsNullOrWhiteSpace(priority)) parts.Add($"Prioridad: {priority}");
        return string.Join(" · ", parts);
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static string Clip(string? value, int max)
    {
        var v = value?.Trim() ?? string.Empty;
        return v.Length > max ? v[..max] : v;
    }
}
