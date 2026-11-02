using BuildingFex.Api.Iam.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Iam.Domain.Model.Aggregates;

/// <summary>
/// Persona con acceso a un condominio además de su dueño: un administrador asignado (opera el edificio)
/// o un miembro de la junta directiva (ve la información, sin modificarla). Un mismo usuario puede ser
/// miembro de varios condominios, por ejemplo una empresa administradora con varios edificios.
/// </summary>
public class BuildingMember
{
    public int Id { get; private set; }

    /// <summary>Tenant: users.id de la cuenta del condominio.</summary>
    public int OwnerAdminId { get; private set; }

    public User? OwnerAdmin { get; private set; }

    public int UserId { get; private set; }

    public User? User { get; private set; }

    /// <summary>admin | board_member</summary>
    public string Role { get; private set; } = UserRoles.BoardMember;

    public DateTimeOffset CreatedAt { get; private set; }

    private BuildingMember()
    {
    }

    public static BuildingMember Create(int ownerAdminId, int userId, string role) => new()
    {
        OwnerAdminId = ownerAdminId,
        UserId = userId,
        Role = MemberRoles.Normalize(role),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    public void ChangeRole(string role) => Role = MemberRoles.Normalize(role);
}

/// <summary>Roles que se pueden asignar a un miembro de un condominio.</summary>
public static class MemberRoles
{
    public const string Admin = UserRoles.Admin;
    public const string Board = UserRoles.BoardMember;

    public static bool IsValid(string? role) => Parse(role) is not null;

    /// <summary>Acepta "admin", "board", "board_member" o "junta".</summary>
    public static string? Parse(string? role) =>
        (role ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "admin" or "administrator" or "administrador" => Admin,
            "board" or "board_member" or "junta" => Board,
            _ => null,
        };

    public static string Normalize(string? role) => Parse(role) ?? Board;
}
