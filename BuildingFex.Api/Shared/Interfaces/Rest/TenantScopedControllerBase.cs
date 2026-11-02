using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Shared.Interfaces.Rest;

/// <summary>
/// Base para controladores multi-tenant (RNF001 / QAS06): el tenant SIEMPRE sale del token
/// (admin → su propio id; residente → su OwnerAdminId). El parámetro ownerAdminId del cliente
/// se acepta por compatibilidad, pero si apunta a otro condominio se responde 403 y se audita.
/// </summary>
public abstract class TenantScopedControllerBase : ControllerBase
{
    public sealed record TenantResolution(CallerContext? Caller, IActionResult? Error);

    private ILogger AuditLogger =>
        HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BuildingFex.TenantAudit");

    protected async Task<TenantResolution> ResolveTenantAsync(
        string? requestedOwnerAdminId,
        CancellationToken ct)
    {
        var accessor = HttpContext.RequestServices.GetRequiredService<ICallerContextAccessor>();
        var caller = await accessor.GetAsync(ct);
        if (caller is null)
        {
            return new TenantResolution(null, StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "TENANT_REQUIRED",
                message = "El usuario autenticado no pertenece a ningún condominio.",
            }));
        }

        if (!string.IsNullOrWhiteSpace(requestedOwnerAdminId) && !caller.OwnsTenant(requestedOwnerAdminId))
            return new TenantResolution(null, ForbiddenTenant(caller, "ownerAdminId", requestedOwnerAdminId));

        return new TenantResolution(caller, null);
    }

    /// <summary>
    /// Filtro por residente: un residente solo puede consultar sus propios datos
    /// (si no envía residentId se usa el suyo). Un admin puede filtrar libremente dentro de su tenant.
    /// </summary>
    protected IActionResult? ResolveResidentScope(
        CallerContext caller,
        string? requestedResidentId,
        out string? effectiveResidentId)
    {
        var requested = string.IsNullOrWhiteSpace(requestedResidentId) ? null : requestedResidentId.Trim();
        if (caller.IsAdmin)
        {
            effectiveResidentId = requested;
            return null;
        }

        effectiveResidentId = caller.UserExternalId;
        if (requested is not null && !string.Equals(requested, caller.UserExternalId, StringComparison.Ordinal))
            return ForbiddenTenant(caller, "residentId", requested);

        return null;
    }

    /// <summary>403 + registro de auditoría del intento de acceso a datos ajenos.</summary>
    protected IActionResult ForbiddenTenant(CallerContext caller, string resource, string? requestedId)
    {
        AuditLogger.LogWarning(
            "Tenant violation: user {UserId} (role {Role}, tenant {Tenant}) tried {Method} {Path} on {Resource}={RequestedId}",
            caller.UserExternalId,
            caller.User.Role,
            caller.TenantExternalId,
            HttpContext.Request.Method,
            HttpContext.Request.Path.Value,
            resource,
            requestedId);

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "FORBIDDEN_TENANT",
            message = "No tienes acceso a este recurso.",
        });
    }

    /// <summary>Lee una propiedad string de un body JSON (null si falta o no es string).</summary>
    protected static string? BodyString(System.Text.Json.JsonElement body, string name) =>
        body.ValueKind == System.Text.Json.JsonValueKind.Object
        && body.TryGetProperty(name, out var prop)
        && prop.ValueKind == System.Text.Json.JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>Entidad con FK int al admin dueño.</summary>
    protected bool BelongsToTenant(CallerContext caller, int ownerAdminId) => ownerAdminId == caller.TenantId;

    /// <summary>Entidad con ownerAdminId externo (string).</summary>
    protected bool BelongsToTenant(CallerContext caller, string? ownerAdminExternalId) =>
        caller.OwnsTenant(ownerAdminExternalId);
}
