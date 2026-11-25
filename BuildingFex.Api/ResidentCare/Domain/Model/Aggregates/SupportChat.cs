using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

namespace BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;

/// <summary>
/// Cabecera de chat (tabla support_chats). Los mensajes viven en support_messages.
/// Escalado: se crea una 2da fila kind=admin; el AI conserva kind=ai y ambos se ligan con LinkedChatId.
/// </summary>
public class SupportChat
{
    public const string KindAi = "ai";
    public const string KindAdmin = "admin";
    public const string SourceResident = "resident";
    public const string SourceAiEscalation = "ai-escalation";

    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string OwnerAdminId { get; private set; } = string.Empty;
    public string ResidentId { get; private set; } = string.Empty;
    public string ResidentName { get; private set; } = string.Empty;
    public string Topic { get; private set; } = string.Empty;
    public string Status { get; private set; } = ConversationStatus.Open;
    public string Kind { get; private set; } = KindAi;
    public string Source { get; private set; } = SourceResident;
    public string? LinkedChatId { get; private set; }

    /// <summary>Incidencia registrada desde esta conversación (Incidents).</summary>
    public string? IncidentExternalId { get; private set; }
    public bool TitleSummarized { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<SupportMessage> Messages { get; private set; } = [];

    private SupportChat()
    {
    }

    public static SupportChat CreateAi(
        string externalId,
        string ownerAdminId,
        string residentId,
        string residentName,
        string topic)
    {
        var now = DateTimeOffset.UtcNow;
        return new SupportChat
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId.Trim(),
            ResidentId = residentId.Trim(),
            ResidentName = residentName.Trim(),
            Topic = string.IsNullOrWhiteSpace(topic) ? "Soporte" : topic.Trim(),
            Status = ConversationStatus.Open,
            Kind = KindAi,
            Source = SourceResident,
            TitleSummarized = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public static SupportChat CreateAdmin(
        string externalId,
        string ownerAdminId,
        string residentId,
        string residentName,
        string topic,
        string source,
        string? linkedAiChatId)
    {
        var now = DateTimeOffset.UtcNow;
        return new SupportChat
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId.Trim(),
            ResidentId = residentId.Trim(),
            ResidentName = residentName.Trim(),
            Topic = string.IsNullOrWhiteSpace(topic) ? "Soporte" : topic.Trim(),
            Status = ConversationStatus.Open,
            Kind = KindAdmin,
            Source = string.IsNullOrWhiteSpace(source) ? SourceResident : source.Trim(),
            LinkedChatId = string.IsNullOrWhiteSpace(linkedAiChatId) ? null : linkedAiChatId.Trim(),
            TitleSummarized = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void SetTopic(string topic, bool summarized = false)
    {
        if (!string.IsNullOrWhiteSpace(topic))
            Topic = topic.Trim();
        if (summarized)
            TitleSummarized = true;
        Touch();
    }

    public void LinkTo(string otherChatExternalId)
    {
        LinkedChatId = otherChatExternalId.Trim();
        Touch();
    }

    public void UpdateStatus(string status)
    {
        var normalized = ConversationStatus.Normalize(status)
            ?? throw new ArgumentException($"Invalid conversation status '{status}'.", nameof(status));
        Status = normalized;
        ClosedAt = normalized == ConversationStatus.Closed ? (ClosedAt ?? DateTimeOffset.UtcNow) : null;
        Touch();
    }

    /// <summary>El residente escribió: la consulta queda pendiente (salvo un chat IA ya derivado).</summary>
    public void MarkPending()
    {
        if (Kind == KindAi && Status == ConversationStatus.Derived && !string.IsNullOrWhiteSpace(LinkedChatId))
            return;
        UpdateStatus(ConversationStatus.Open);
    }

    /// <summary>Respondió el asistente o el administrador (US032: "Atendido").</summary>
    public void MarkAnswered()
    {
        if (Kind == KindAi && Status == ConversationStatus.Derived) return;
        UpdateStatus(ConversationStatus.Answered);
    }

    /// <summary>Chat IA derivado a la junta / administración.</summary>
    public void Derive(string adminChatExternalId)
    {
        LinkTo(adminChatExternalId);
        UpdateStatus(ConversationStatus.Derived);
    }

    public void Close() => UpdateStatus(ConversationStatus.Closed);

    public void LinkIncident(string incidentExternalId)
    {
        IncidentExternalId = incidentExternalId.Trim();
        Touch();
    }

    public SupportMessage AddMessage(
        string authorRole,
        string body,
        string? authorName = null,
        string? citedDocumentId = null,
        string? citedSource = null)
        => SupportMessage.Create(this, authorRole, body, authorName, citedDocumentId, citedSource);

    public SupportMessage AddMessage(Sender sender, string body, string? authorName = null)
        => AddMessage(SenderRoles.ToAuthorRole(sender, Kind), body, authorName);

    /// <summary>Copia un mensaje de otro chat (transcript de la derivación) conservando fecha y cita.</summary>
    public SupportMessage CopyMessage(SupportMessage source)
    {
        var sender = source.GetSender();
        var role = sender is null ? source.AuthorRole : SenderRoles.ToAuthorRole(sender.Value, Kind);
        return SupportMessage.Create(
            this, role, source.Body, source.AuthorName, source.CitedDocumentId, source.CitedSource, source.CreatedAt);
    }

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}
