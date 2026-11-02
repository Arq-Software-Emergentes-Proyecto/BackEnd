namespace BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;

/// <summary>
/// Fila de mensaje (tabla support_messages). Append-only — evita lost update del blob JSON.
/// </summary>
public class SupportMessage
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int ChatId { get; private set; }
    public SupportChat? Chat { get; private set; }
    /// <summary>user | assistant | admin | resident</summary>
    public string AuthorRole { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string? AuthorName { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private SupportMessage()
    {
    }

    public static SupportMessage Create(
        SupportChat chat,
        string authorRole,
        string body,
        string? authorName = null,
        DateTimeOffset? createdAt = null)
    {
        var msg = new SupportMessage
        {
            // Guid: varias filas por milisegundo al copiar el transcript en una derivación.
            ExternalId = $"msg-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}",
            Chat = chat,
            ChatId = chat.Id,
            AuthorRole = authorRole.Trim(),
            Body = body.Trim(),
            AuthorName = string.IsNullOrWhiteSpace(authorName) ? null : authorName.Trim(),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        chat.Messages.Add(msg);
        chat.Touch();
        return msg;
    }

    public ValueObjects.Sender? GetSender() => ValueObjects.SenderRoles.FromAuthorRole(AuthorRole);
}
