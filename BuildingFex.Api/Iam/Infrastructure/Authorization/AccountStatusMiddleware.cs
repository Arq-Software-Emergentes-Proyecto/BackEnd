using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Domain.Repositories;
using System.Security.Claims;

namespace BuildingFex.Api.Iam.Infrastructure.Authorization;

/// <summary>
/// Corta todo request autenticado de una cuenta DEACTIVATED con 403 ACCOUNT_DEACTIVATED (Tabla 52),
/// aunque su idToken de Firebase siga vigente hasta 1 h. Usa el usuario ya resuelto al validar el token
/// (HttpContext.Items) o lo resuelve por uid, igual que <see cref="CallerContextAccessor"/>.
/// </summary>
public sealed class AccountStatusMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IUserRepository users)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            var user = context.Items.TryGetValue(CallerContextAccessor.HttpItemKey, out var item) ? item as User : null;
            if (user is null)
            {
                var uid = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? principal.FindFirstValue("user_id")
                          ?? principal.FindFirstValue("sub");
                if (!string.IsNullOrWhiteSpace(uid))
                {
                    user = await CallerContextAccessor.FindUserByFirebaseUidAsync(users, uid, context.RequestAborted);
                    if (user is not null)
                        context.Items[CallerContextAccessor.HttpItemKey] = user;
                }
            }

            if (user is not null && !user.IsActive)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = AccountStatus.DeactivatedErrorCode,
                    message = "Tu cuenta fue desactivada por la administración del condominio.",
                }, context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}

public static class AccountStatusMiddlewareExtensions
{
    /// <summary>Registrar después de UseAuthentication y antes de UseAuthorization.</summary>
    public static IApplicationBuilder UseAccountStatusGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<AccountStatusMiddleware>();
}
