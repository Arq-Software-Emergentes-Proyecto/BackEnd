using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BuildingFex.Api.Iam.Infrastructure.Authorization;

/// <summary>
/// Políticas de autorización por rol (RNF001). El rol viene del claim "role"
/// (custom claim de Firebase o, si falta, de la fila users — ver FirebaseAuthenticationExtensions).
/// La tenencia (ownerAdminId) se valida aparte en <see cref="Application.Internal.Tenancy.ICallerContextAccessor"/>.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Gestión del condominio: dueño, administrador asignado o junta directiva. La junta solo lee:
    /// <see cref="BoardReadOnlyMiddleware"/> rechaza sus escrituras antes de llegar al controlador.
    /// </summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>Solo residentes invitados.</summary>
    public const string ResidentOnly = "ResidentOnly";

    /// <summary>Usuario aprovisionado con rol admin o resident.</summary>
    public const string AdminOrResident = "AdminOrResident";

    public const string RoleAdmin = "admin";
    public const string RoleResident = "resident";
    public const string RoleBoard = "board_member";

    public static bool HasRole(ClaimsPrincipal user, string role) =>
        user.Claims.Any(c =>
            (c.Type == "role" || c.Type == ClaimTypes.Role) &&
            string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));

    public static IServiceCollection AddBuildingFexAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AdminOnly, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasRole(ctx.User, RoleAdmin) || HasRole(ctx.User, RoleBoard)));

            options.AddPolicy(ResidentOnly, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasRole(ctx.User, RoleResident)));

            options.AddPolicy(AdminOrResident, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx =>
                    HasRole(ctx.User, RoleAdmin) || HasRole(ctx.User, RoleResident) || HasRole(ctx.User, RoleBoard)));
        });

        return services;
    }
}
