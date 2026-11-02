using BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;

namespace BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

/// <summary>Autor del mensaje (informe 5.9.1: RESIDENT, ASSISTANT, ADMIN).</summary>
public enum Sender
{
    Resident,
    Assistant,
    Admin,
}

/// <summary>
/// Mapeo Sender ↔ support_messages.author_role (contrato existente): en chats IA el residente
/// se guarda como "user" y en chats admin como "resident"; "ai" es legado de "assistant".
/// </summary>
public static class SenderRoles
{
    public const string ResidentInAiChat = "user";
    public const string Resident = "resident";
    public const string Assistant = "assistant";
    public const string Admin = "admin";

    public static string ToAuthorRole(Sender sender, string chatKind) => sender switch
    {
        Sender.Resident => chatKind == SupportChat.KindAi ? ResidentInAiChat : Resident,
        Sender.Assistant => Assistant,
        Sender.Admin => Admin,
        _ => Resident,
    };

    public static Sender? FromAuthorRole(string? authorRole) => (authorRole ?? string.Empty).Trim() switch
    {
        ResidentInAiChat or Resident => Sender.Resident,
        Assistant or "ai" => Sender.Assistant,
        Admin => Sender.Admin,
        _ => null,
    };
}
