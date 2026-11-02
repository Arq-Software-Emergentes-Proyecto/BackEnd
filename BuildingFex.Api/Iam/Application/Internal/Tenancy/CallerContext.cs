using System.Security.Claims;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Profiles.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Iam.Application.Internal.Tenancy;

/// <summary>Rol del usuario en el condominio activo.</summary>
public static class TenantRoles
{
    /// <summary>Dueño de la cuenta del condominio (o de la cuenta matriz de Portafolio).</summary>
    public const string Owner = "owner";

    /// <summary>Administrador asignado por el dueño (por ejemplo, la empresa que contrató la junta).</summary>
    public const string Admin = "admin";

    /// <summary>Junta directiva: ve la información del condominio, no la modifica.</summary>
    public const string Board = "board_member";

    public const string Resident = "resident";
}

/// <summary>Condominio al que el usuario tiene acceso y con qué rol.</summary>
public sealed record AccessibleTenant(User Tenant, string Role);

/// <summary>
/// Usuario autenticado + condominio (tenant) activo, resueltos desde la BD a partir del uid del token
/// Firebase. Nunca desde query/body (RNF001, QAS06). Un usuario puede tener acceso a varios condominios
/// (Portafolio o administrador asignado); el cliente elige cuál con la cabecera X-Building-Id y el
/// servidor solo la acepta si el usuario tiene acceso a ese condominio.
/// </summary>
public sealed class CallerContext(
    User user,
    User tenantAdmin,
    string tenantRole,
    IReadOnlyList<AccessibleTenant>? accessibleTenants = null)
{
    public User User { get; } = user;

    /// <summary>Cuenta del condominio activo (tenant de sus datos).</summary>
    public User TenantAdmin { get; } = tenantAdmin;

    /// <summary>owner | admin | board_member | resident en el condominio activo.</summary>
    public string TenantRole { get; } = tenantRole;

    public IReadOnlyList<AccessibleTenant> AccessibleTenants { get; } =
        accessibleTenants ?? [new AccessibleTenant(tenantAdmin, tenantRole)];

    /// <summary>
    /// Acceso de gestión al condominio. La junta también lo tiene para leer: el middleware
    /// BoardReadOnlyMiddleware rechaza cualquier escritura suya antes de llegar al controlador.
    /// </summary>
    public bool IsAdmin => TenantRole is TenantRoles.Owner or TenantRoles.Admin or TenantRoles.Board;

    /// <summary>Puede modificar datos del condominio (dueño o administrador asignado).</summary>
    public bool CanManage => TenantRole is TenantRoles.Owner or TenantRoles.Admin;

    public bool IsBoard => TenantRole == TenantRoles.Board;
    public bool IsResident => TenantRole == TenantRoles.Resident;

    /// <summary>Cuenta que paga la suscripción (la matriz en Portafolio).</summary>
    public User BillingAdmin => TenantAdmin.BillingAccount;

    /// <summary>ownerAdminId externo (uid Firebase del admin) del tenant.</summary>
    public string TenantExternalId => TenantAdmin.ExternalId;

    /// <summary>FK interna users.id del admin del tenant.</summary>
    public int TenantId => TenantAdmin.Id;

    /// <summary>Id externo del usuario (uid del admin o "resident-…" del residente).</summary>
    public string UserExternalId => User.ExternalId;

    public bool OwnsTenant(string? ownerAdminExternalId) =>
        string.Equals((ownerAdminExternalId ?? string.Empty).Trim(), TenantExternalId, StringComparison.Ordinal);
}

public interface ICallerContextAccessor
{
    /// <summary>Null si no hay usuario autenticado o no está aprovisionado en un tenant.</summary>
    Task<CallerContext?> GetAsync(CancellationToken ct = default);
}

public class CallerContextAccessor(
    IHttpContextAccessor httpContextAccessor,
    IUserRepository userRepository,
    AppDbContext db) : ICallerContextAccessor
{
    /// <summary>HttpContext.Items: User cargado durante la validación del token (evita doble consulta).</summary>
    public const string HttpItemKey = "bf.caller.user";

    /// <summary>Cabecera con la que el cliente elige el condominio activo (ownerAdminId externo).</summary>
    public const string BuildingHeader = "X-Building-Id";

    private bool _resolved;
    private CallerContext? _cached;

    public async Task<CallerContext?> GetAsync(CancellationToken ct = default)
    {
        if (_resolved) return _cached;

        var http = httpContextAccessor.HttpContext;
        var principal = http?.User;
        if (http is null || principal?.Identity?.IsAuthenticated != true)
        {
            _resolved = true;
            return null;
        }

        var user = http.Items.TryGetValue(HttpItemKey, out var item) ? item as User : null;
        if (user is null)
        {
            var uid = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? principal.FindFirstValue("user_id")
                      ?? principal.FindFirstValue("sub");
            if (!string.IsNullOrWhiteSpace(uid))
                user = await FindUserByFirebaseUidAsync(userRepository, uid, ct);
        }

        _resolved = true;
        // Cuenta desactivada: sin contexto (el middleware AccountStatusGuard ya responde 403 ACCOUNT_DEACTIVATED).
        if (user is null || !user.IsActive) return null;

        var tenants = await ResolveAccessibleTenantsAsync(user, ct);
        if (tenants.Count == 0) return null;

        var requested = http.Request.Headers[BuildingHeader].ToString().Trim();
        AccessibleTenant? active;
        if (requested.Length > 0)
        {
            active = tenants.FirstOrDefault(t => string.Equals(t.Tenant.ExternalId, requested, StringComparison.Ordinal));
            // Un condominio al que no tiene acceso: sin contexto (403), nunca se cae a otro en silencio.
            if (active is null) return null;
        }
        else
        {
            active = tenants[0];
        }

        _cached = new CallerContext(user, active.Tenant, active.Role, tenants);
        return _cached;
    }

    /// <summary>
    /// Condominios a los que el usuario tiene acceso, el predeterminado primero:
    /// residente → el de su departamento; dueño → el propio y los edificios de su Portafolio;
    /// administrador asignado o junta → los condominios donde es miembro.
    /// </summary>
    private async Task<IReadOnlyList<AccessibleTenant>> ResolveAccessibleTenantsAsync(User user, CancellationToken ct)
    {
        if (user.Role == UserRoles.Resident)
        {
            var owner = user.OwnerAdmin;
            return owner is not null && owner.Role == UserRoles.Admin
                ? [new AccessibleTenant(owner, TenantRoles.Resident)]
                : [];
        }

        if (user.Role != UserRoles.Admin && user.Role != UserRoles.BoardMember) return [];
        if (user.IsManagedBuildingAccount) return [];

        var memberships = await db.Set<BuildingMember>()
            .AsNoTracking()
            .Include(m => m.OwnerAdmin).ThenInclude(o => o!.ParentAdmin)
            .Where(m => m.UserId == user.Id)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        var result = new List<AccessibleTenant>();
        if (user.Role == UserRoles.Admin)
        {
            // La propia cuenta: siempre, salvo un administrador asignado que todavía no registró edificio propio.
            var ownsBuilding = await db.Set<Building>().AnyAsync(b => b.OwnerAdminId == user.Id, ct);
            if (ownsBuilding || memberships.Count == 0)
                result.Add(new AccessibleTenant(user, TenantRoles.Owner));

            var portfolio = await db.Set<User>()
                .AsNoTracking()
                .Include(u => u.ParentAdmin)
                .Where(u => u.ParentAdminId == user.Id && u.Role == UserRoles.Admin)
                .OrderBy(u => u.Id)
                .ToListAsync(ct);
            result.AddRange(portfolio.Select(b => new AccessibleTenant(b, TenantRoles.Owner)));
        }

        foreach (var m in memberships)
        {
            if (m.OwnerAdmin is null || m.OwnerAdmin.Role != UserRoles.Admin) continue;
            if (result.Any(t => t.Tenant.Id == m.OwnerAdminId)) continue;
            // Un usuario con rol de junta nunca administra, aunque la fila diga otra cosa.
            var role = user.Role == UserRoles.BoardMember ? TenantRoles.Board
                : m.Role == MemberRoles.Admin ? TenantRoles.Admin : TenantRoles.Board;
            result.Add(new AccessibleTenant(m.OwnerAdmin, role));
        }

        return result;
    }

    /// <summary>
    /// Admins y junta: users.external_id = uid Firebase. Residentes: users.google_sub = uid Firebase
    /// (Google o email/contraseña). No se resuelve por email: un email no verificado no prueba identidad.
    /// </summary>
    public static async Task<User?> FindUserByFirebaseUidAsync(
        IUserRepository users,
        string uid,
        CancellationToken ct)
    {
        var clean = (uid ?? string.Empty).Trim();
        if (clean.Length == 0) return null;

        var byExternal = await users.FindByExternalIdAsync(clean, ct);
        if (byExternal is not null
            && (byExternal.Role == UserRoles.Admin || byExternal.Role == UserRoles.BoardMember)
            && !byExternal.IsManagedBuildingAccount)
            return byExternal;

        return await users.FindByGoogleSubAsync(clean, ct);
    }
}
