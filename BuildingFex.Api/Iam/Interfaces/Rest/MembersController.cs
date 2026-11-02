using System.Security.Claims;
using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Profiles.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IamUser = BuildingFex.Api.Iam.Domain.Model.Aggregates.User;

namespace BuildingFex.Api.Iam.Interfaces.Rest;

/// <summary>
/// Equipo de gestión del condominio activo: su dueño, los administradores asignados y la junta directiva.
/// Si la junta creó la cuenta, asigna aquí al administrador que opera el edificio; si la cuenta es de una
/// empresa administradora, asigna a los miembros de la junta para que vean la información.
/// </summary>
[ApiController]
[Route("api/v1/members")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class MembersController(AppDbContext db, ILogger<MembersController> logger) : TenantScopedControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return tenant.Error;
        var caller = tenant.Caller!;

        var owner = caller.TenantAdmin.ParentAdmin ?? caller.TenantAdmin;
        var members = await db.Set<BuildingMember>()
            .AsNoTracking()
            .Include(m => m.User)
            .Where(m => m.OwnerAdminId == caller.TenantId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var invitations = await db.Set<MemberInvitation>()
            .AsNoTracking()
            .Where(i => i.OwnerAdminId == caller.TenantId && i.AcceptedAt == null && i.RevokedAt == null)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return Ok(new
        {
            ownerAdminId = caller.TenantExternalId,
            myRole = caller.TenantRole,
            canManage = caller.CanManage,
            owner = new
            {
                id = owner.ExternalId,
                name = owner.Name,
                email = owner.Email,
                accountKind = owner.AccountKind ?? IamUser.AccountKindAdministrator,
            },
            members = members.Where(m => m.User is not null).Select(m => new
            {
                id = m.User!.ExternalId,
                name = m.User.Name,
                email = m.User.Email,
                role = m.Role,
                since = m.CreatedAt.ToString("o"),
            }),
            // El enlace solo lo ve quien puede gestionar: la junta no reparte accesos.
            invitations = invitations.Select(i => ToInvitationResource(i, now, includeToken: caller.CanManage)),
        });
    }

    /// <summary>Invita a un administrador o a un miembro de la junta (enlace de un solo uso, 7 días).</summary>
    [HttpPost("invitations")]
    public async Task<IActionResult> Invite([FromBody] InviteMemberResource? resource, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return tenant.Error;
        var caller = tenant.Caller!;
        if (!caller.CanManage) return ManageRequired();

        var role = MemberRoles.Parse(resource?.Role);
        if (role is null)
            return BadRequest(new { code = "INVALID_MEMBER_ROLE", message = "El rol debe ser administrador o junta directiva." });
        if (!Email.TryCreate(resource?.Email, out var email))
            return BadRequest(new { code = "INVALID_EMAIL", message = "Ingresa un correo válido." });

        var owner = caller.TenantAdmin.ParentAdmin ?? caller.TenantAdmin;
        if (string.Equals(owner.Email, email!.Value, StringComparison.OrdinalIgnoreCase))
            return Conflict(new { code = "ALREADY_MEMBER", message = "Ese correo es del dueño de la cuenta." });

        var alreadyMember = await db.Set<BuildingMember>()
            .AnyAsync(m => m.OwnerAdminId == caller.TenantId && m.User!.Email == email.Value, ct);
        if (alreadyMember)
            return Conflict(new { code = "ALREADY_MEMBER", message = "Esa persona ya tiene acceso a este condominio." });

        var resident = await db.Set<User>()
            .AnyAsync(u => u.Role == UserRoles.Resident && u.Email == email.Value, ct);
        if (resident)
        {
            return Conflict(new
            {
                code = "EMAIL_USED_BY_RESIDENT",
                message = "Ese correo pertenece a una cuenta de residente. Usa otro correo para el acceso de gestión.",
            });
        }

        // Reenviar: la invitación anterior pendiente al mismo correo deja de valer.
        var previous = await db.Set<MemberInvitation>()
            .Where(i => i.OwnerAdminId == caller.TenantId && i.Email == email.Value && i.AcceptedAt == null && i.RevokedAt == null)
            .ToListAsync(ct);
        previous.ForEach(i => i.Revoke());

        var invitation = MemberInvitation.Issue(caller.TenantId, email.Value, resource?.Name, role, caller.User.Id);
        db.Set<MemberInvitation>().Add(invitation);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Member invitation {Invitation} ({Role}) issued for tenant {Tenant} by {User}",
            invitation.ExternalId, invitation.Role, caller.TenantExternalId, caller.UserExternalId);
        return StatusCode(StatusCodes.Status201Created, ToInvitationResource(invitation, DateTimeOffset.UtcNow, includeToken: true));
    }

    [HttpDelete("invitations/{id}")]
    public async Task<IActionResult> RevokeInvitation(string id, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return tenant.Error;
        var caller = tenant.Caller!;
        if (!caller.CanManage) return ManageRequired();

        var invitation = await db.Set<MemberInvitation>().FirstOrDefaultAsync(i => i.ExternalId == id, ct);
        if (invitation is null)
            return NotFound(new { code = "INVITATION_NOT_FOUND", message = "Invitación no encontrada." });
        if (invitation.OwnerAdminId != caller.TenantId)
            return ForbiddenTenant(caller, "invitationId", id);

        invitation.Revoke();
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Quita el acceso de un administrador asignado o de un miembro de la junta.</summary>
    [HttpDelete("{userId}")]
    public async Task<IActionResult> Remove(string userId, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return tenant.Error;
        var caller = tenant.Caller!;
        if (!caller.CanManage) return ManageRequired();

        var member = await db.Set<BuildingMember>()
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.OwnerAdminId == caller.TenantId && m.User!.ExternalId == userId, ct);
        if (member is null)
            return NotFound(new { code = "MEMBER_NOT_FOUND", message = "Esa persona no es miembro de este condominio." });
        // Un administrador asignado no retira su propio acceso: lo hace el dueño de la cuenta.
        if (member.UserId == caller.User.Id && caller.TenantRole != TenantRoles.Owner)
            return Conflict(new { code = "CANNOT_REMOVE_SELF", message = "Pide al dueño de la cuenta que retire tu acceso." });

        db.Set<BuildingMember>().Remove(member);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IActionResult ManageRequired() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "MANAGE_REQUIRED",
            message = "Solo el dueño de la cuenta o un administrador asignado puede gestionar los accesos.",
        });

    private static object ToInvitationResource(MemberInvitation i, DateTimeOffset now, bool includeToken) => new
    {
        id = i.ExternalId,
        email = i.Email,
        name = i.Name,
        role = i.Role,
        expiresAt = i.ExpiresAt.ToString("o"),
        status = i.IsExpired(now) ? "expired" : "pending",
        token = includeToken && i.IsPending(now) ? i.Token : null,
    };
}

public record InviteMemberResource(string? Email, string? Role, string? Name = null);

public record AcceptMemberInvitationResource(string? Name = null);

/// <summary>
/// Enlace de invitación al equipo de gestión. Consultarlo es público (muestra el edificio y el rol);
/// aceptarlo exige iniciar sesión con el mismo correo invitado.
/// </summary>
[ApiController]
[Route("api/v1/member-invitations")]
public class MemberInvitationsController(
    AppDbContext db,
    IUserRepository users,
    ILogger<MemberInvitationsController> logger) : ControllerBase
{
    [HttpGet("{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        var invitation = await FindAsync(token, ct);
        if (invitation is null)
            return NotFound(new { code = "INVITATION_NOT_FOUND", message = "El enlace no es válido." });

        var building = await db.Set<Building>().AsNoTracking()
            .Where(b => b.OwnerAdminId == invitation.OwnerAdminId)
            .OrderBy(b => b.Id)
            .FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return Ok(new
        {
            buildingName = building?.Name ?? invitation.OwnerAdmin?.Name ?? "Condominio",
            buildingAddress = building?.Address,
            role = invitation.Role,
            email = invitation.Email,
            name = invitation.Name,
            expiresAt = invitation.ExpiresAt.ToString("o"),
            status = invitation.AcceptedAt is not null ? "accepted"
                : invitation.RevokedAt is not null ? "revoked"
                : invitation.IsExpired(now) ? "expired"
                : "pending",
        });
    }

    /// <summary>
    /// Acepta la invitación con la cuenta Firebase con la que se inició sesión (correo o Google). Si la persona
    /// no tenía cuenta en BuildingFex se crea con el rol invitado; si ya era administradora de otro condominio,
    /// suma este a los que gestiona.
    /// </summary>
    [HttpPost("{token}/accept")]
    [Authorize]
    public async Task<IActionResult> Accept(string token, [FromBody] AcceptMemberInvitationResource? resource, CancellationToken ct)
    {
        var invitation = await FindAsync(token, ct, track: true);
        var now = DateTimeOffset.UtcNow;
        if (invitation is null || invitation.RevokedAt is not null)
            return NotFound(new { code = "INVITATION_NOT_FOUND", message = "El enlace no es válido." });
        if (invitation.AcceptedAt is not null)
            return Conflict(new { code = "INVITATION_ALREADY_USED", message = "Esta invitación ya se usó." });
        if (invitation.IsExpired(now))
            return BadRequest(new { code = "INVITATION_EXPIRED", message = "La invitación venció. Pide una nueva." });

        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("user_id") ?? User.FindFirstValue("sub");
        var email = Email.Normalize(User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email"));
        if (string.IsNullOrWhiteSpace(uid))
            return Unauthorized(new { code = "UNAUTHORIZED", message = "Inicia sesión para aceptar la invitación." });
        if (!string.Equals(email, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                code = "INVITATION_EMAIL_MISMATCH",
                message = $"Esta invitación es para {invitation.Email}. Inicia sesión con ese correo.",
            });
        }

        var user = await CallerContextAccessor.FindUserByFirebaseUidAsync(users, uid, ct);
        if (user is not null && user.Role == UserRoles.Resident)
        {
            return Conflict(new
            {
                code = "RESIDENT_ACCOUNT",
                message = "Esta cuenta es de residente. Usa otro correo para el acceso de gestión.",
            });
        }

        if (user is not null && user.Id == invitation.OwnerAdminId)
            return Conflict(new { code = "ALREADY_OWNER", message = "Ya eres el dueño de este condominio." });

        var name = string.IsNullOrWhiteSpace(resource?.Name)
            ? (string.IsNullOrWhiteSpace(invitation.Name) ? email.Split('@')[0] : invitation.Name)
            : resource!.Name!.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (user is null)
        {
            user = invitation.Role == MemberRoles.Admin
                ? IamUser.CreateAdmin(uid, name, email, IamUser.FirebasePasswordPlaceholder, authProvider: IamUser.AuthProviderFirebase)
                : IamUser.CreateBoardMember(uid, name, email);
            db.Set<User>().Add(user);
            if (!await db.Set<Profile>().AnyAsync(p => p.UserExternalId == uid, ct))
                db.Set<Profile>().Add(Profile.Create(uid, name, email, null, null, null, null));
            await db.SaveChangesAsync(ct);
        }

        // Una persona con rol de junta solo puede ser junta; un administrador puede ser invitado como junta.
        var role = user.Role == UserRoles.BoardMember ? MemberRoles.Board : invitation.Role;
        var member = await db.Set<BuildingMember>()
            .FirstOrDefaultAsync(m => m.OwnerAdminId == invitation.OwnerAdminId && m.UserId == user.Id, ct);
        if (member is null)
            db.Set<BuildingMember>().Add(BuildingMember.Create(invitation.OwnerAdminId, user.Id, role));
        else
            member.ChangeRole(role);

        invitation.Accept();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Member invitation {Invitation} accepted by {User} as {Role}", invitation.ExternalId, user.ExternalId, role);
        return Ok(new
        {
            ownerAdminId = invitation.OwnerAdmin?.ExternalId,
            role,
            user = new { id = user.ExternalId, name = user.Name, email = user.Email, role = user.Role },
        });
    }

    private Task<MemberInvitation?> FindAsync(string token, CancellationToken ct, bool track = false)
    {
        var clean = (token ?? string.Empty).Trim();
        if (clean.Length < 32) return Task.FromResult<MemberInvitation?>(null);
        var query = db.Set<MemberInvitation>().Include(i => i.OwnerAdmin).Where(i => i.Token == clean);
        return (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }
}
