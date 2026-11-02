namespace BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

/// <summary>
/// Estado de la consulta (informe 5.9.1: OPEN, ANSWERED, DERIVED, CLOSED). Se guarda en minúsculas
/// en support_chats.status. En la bandeja del admin (US032): open = pendiente, answered = atendido.
/// </summary>
public static class ConversationStatus
{
    /// <summary>Pendiente: el residente escribió y nadie respondió aún.</summary>
    public const string Open = "open";

    /// <summary>Atendido: respondió el asistente (chat IA) o el administrador (chat admin).</summary>
    public const string Answered = "answered";

    /// <summary>Chat IA derivado a la junta / administración (existe chat admin ligado).</summary>
    public const string Derived = "derived";

    public const string Closed = "closed";

    public static IReadOnlyList<string> All { get; } = [Open, Answered, Derived, Closed];

    /// <summary>Acepta los valores canónicos y alias de la UI (pending/attended/atendido…). Null si no es válido.</summary>
    public static string? Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v switch
        {
            Open or "pending" or "pendiente" => Open,
            Answered or "attended" or "atendido" or "atendida" => Answered,
            Derived or "escalated" or "derivado" or "derivada" => Derived,
            Closed or "cerrado" or "cerrada" => Closed,
            _ => null,
        };
    }

    public static bool IsPending(string? status) =>
        string.IsNullOrWhiteSpace(status) || Normalize(status) == Open;
}
