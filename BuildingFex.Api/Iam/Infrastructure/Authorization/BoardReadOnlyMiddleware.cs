using BuildingFex.Api.Iam.Application.Internal.Tenancy;

namespace BuildingFex.Api.Iam.Infrastructure.Authorization;

/// <summary>
/// La junta directiva ve la información del condominio pero no la modifica: cualquier escritura
/// (POST, PUT, PATCH, DELETE) de un usuario cuyo rol en el condominio activo es junta responde
/// 403 BOARD_READ_ONLY. Las únicas excepciones son las de su propia cuenta (aceptar una invitación).
/// </summary>
public sealed class BoardReadOnlyMiddleware(RequestDelegate next)
{
    private static readonly string[] AllowedWritePrefixes =
    [
        "/api/v1/member-invitations/",
        "/api/v1/authentication/",
        "/hubs/",
    ];

    public async Task InvokeAsync(HttpContext context, ICallerContextAccessor callers)
    {
        if (context.User?.Identity?.IsAuthenticated == true && IsWrite(context.Request.Method))
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var exempt = AllowedWritePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (!exempt)
            {
                var caller = await callers.GetAsync(context.RequestAborted);
                if (caller is not null && caller.IsBoard)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "BOARD_READ_ONLY",
                        message = "La junta directiva puede ver la información del condominio, pero no modificarla.",
                    }, context.RequestAborted);
                    return;
                }
            }
        }

        await next(context);
    }

    private static bool IsWrite(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
}

public static class BoardReadOnlyMiddlewareExtensions
{
    /// <summary>Registrar después de UseAccountStatusGuard y antes de UseAuthorization.</summary>
    public static IApplicationBuilder UseBoardReadOnlyGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<BoardReadOnlyMiddleware>();
}
