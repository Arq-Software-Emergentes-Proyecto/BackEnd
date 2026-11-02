namespace BuildingFex.Api.Iam.Domain.Model.ValueObjects;

/// <summary>
/// Role (Tabla 52): ADMIN, BOARD_MEMBER, RESIDENT, STAFF. Los valores guardados en users.role y en los
/// custom claims siguen siendo los históricos en minúsculas ("admin", "resident"); hoy solo esos dos
/// se asignan. <see cref="ToDomainName"/> da el nombre del modelo.
/// </summary>
public static class UserRoles
{
    public const string Admin = "admin";
    public const string BoardMember = "board_member";
    public const string Resident = "resident";
    public const string Staff = "staff";

    public static readonly IReadOnlyList<string> All = [Admin, BoardMember, Resident, Staff];

    public static string ToDomainName(string? storedRole) =>
        (storedRole ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Admin => "ADMIN",
            BoardMember => "BOARD_MEMBER",
            Resident => "RESIDENT",
            Staff => "STAFF",
            _ => "UNKNOWN",
        };

    public static string? FromDomainName(string? domainName) =>
        (domainName ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "ADMIN" => Admin,
            "BOARD_MEMBER" => BoardMember,
            "RESIDENT" => Resident,
            "STAFF" => Staff,
            _ => null,
        };
}
