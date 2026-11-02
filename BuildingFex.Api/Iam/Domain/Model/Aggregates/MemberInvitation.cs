namespace BuildingFex.Api.Iam.Domain.Model.Aggregates;

/// <summary>
/// Invitación de un solo uso para sumar un administrador o un miembro de la junta a un condominio.
/// Vence a los 7 días y solo la puede aceptar quien inicie sesión con el mismo correo invitado.
/// </summary>
public class MemberInvitation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public User? OwnerAdmin { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;

    /// <summary>admin | board_member</summary>
    public string Role { get; private set; } = MemberRoles.Board;

    public string Token { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public int InvitedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private MemberInvitation()
    {
    }

    public static MemberInvitation Issue(int ownerAdminId, string email, string? name, string role, int invitedByUserId)
    {
        var now = DateTimeOffset.UtcNow;
        return new MemberInvitation
        {
            ExternalId = "minv-" + Guid.NewGuid().ToString("N")[..16],
            OwnerAdminId = ownerAdminId,
            Email = ValueObjects.Email.Create(email).Value,
            Name = (name ?? string.Empty).Trim(),
            Role = MemberRoles.Normalize(role),
            Token = User.GenerateInviteToken() + User.GenerateInviteToken(),
            ExpiresAt = now.Add(Lifetime),
            InvitedByUserId = invitedByUserId,
            CreatedAt = now,
        };
    }

    public bool IsPending(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;

    public bool IsExpired(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && ExpiresAt <= now;

    public void Accept() => AcceptedAt = DateTimeOffset.UtcNow;

    public void Revoke() => RevokedAt ??= DateTimeOffset.UtcNow;
}
