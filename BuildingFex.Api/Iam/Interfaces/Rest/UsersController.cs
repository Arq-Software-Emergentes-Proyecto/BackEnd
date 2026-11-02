using System.Security.Claims;
using BuildingFex.Api.Iam.Application.CommandServices;
using BuildingFex.Api.Iam.Application.QueryServices;
using BuildingFex.Api.Iam.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Commands;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Iam.Interfaces.Rest.Resources;
using BuildingFex.Api.Iam.Interfaces.Rest.Transform;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Iam.Interfaces.Rest;

/// <summary>
/// Users API — solo admin; tenancy forzada por token.
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController(
    IUserQueryService userQueryService,
    IUserCommandService userCommandService,
    IUserRepository userRepository) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? role,
        [FromQuery] string? email,
        [FromQuery] string? code,
        CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        // Lista solo residentes del condominio del admin autenticado.
        var users = await userQueryService.SearchAsync(
            email,
            string.IsNullOrWhiteSpace(role) ? "resident" : role,
            code,
            admin.ExternalId,
            ct);
        return Ok(users.Select(UserResourceAssembler.ToResource));
    }

    [HttpGet("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetById(string id, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var user = await userQueryService.GetByExternalIdAsync(id, ct);
        if (user is null) return NotFound();
        if (!BelongsToAdmin(user, admin))
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN_TENANT" });

        return Ok(UserResourceAssembler.ToResource(user));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserCompatResource resource, CancellationToken ct)
    {
        if (string.Equals(resource.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            // Auto-aprovision Firebase: el uid del token debe coincidir con resource.Id.
            var uid = ResolveUid();
            if (string.IsNullOrWhiteSpace(uid) ||
                string.IsNullOrWhiteSpace(resource.Id) ||
                !string.Equals(uid, resource.Id.Trim(), StringComparison.Ordinal))
            {
                return Unauthorized(new
                {
                    code = "ADMIN_UID_MISMATCH",
                    message = "El id del body debe coincidir con el uid del Bearer.",
                });
            }

            if (string.IsNullOrWhiteSpace(resource.Password) && !string.IsNullOrWhiteSpace(resource.Id))
            {
                var upsert = await userCommandService.Handle(
                    new UpsertFirebaseAdminCommand(
                        resource.Id,
                        resource.Name ?? string.Empty,
                        ResolveEmail() ?? string.Empty,
                        resource.Dni,
                        resource.Address,
                        resource.Company,
                        resource.Ruc),
                    ct);

                if (upsert.IsFailure)
                {
                    if (upsert.Error is IamError.AdminBootstrapClosed)
                    {
                        return Conflict(new
                        {
                            code = "ADMIN_BOOTSTRAP_CLOSED",
                            message = upsert.Message,
                        });
                    }

                    return BadRequest(new { code = MapErrorCode(upsert.Error), message = upsert.Message });
                }

                return StatusCode(StatusCodes.Status201Created, UserResourceAssembler.ToResource(upsert.Value!));
            }

            return BadRequest(new
            {
                code = "USE_PROFILES",
                message = "Registro admin vía POST /api/v1/profiles (Firebase). Compat password bootstrap deshabilitado.",
            });
        }

        if (!string.Equals(resource.Role, "resident", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "UNSUPPORTED_ROLE", message = "Role must be 'resident' or 'admin'." });

        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var createResult = await userCommandService.Handle(
            new CreateResidentCommand(
                resource.Id ?? string.Empty,
                resource.Name ?? string.Empty,
                resource.Floor ?? string.Empty,
                resource.Code ?? string.Empty,
                admin.ExternalId, // forzado — ignora body.ownerAdminId
                resource.AdmissionDate,
                resource.BillFromNextMonth ?? false),
            ct);

        if (createResult.IsFailure)
            return BadRequest(new { code = MapErrorCode(createResult.Error), message = createResult.Message });

        return StatusCode(StatusCodes.Status201Created, UserResourceAssembler.ToResource(createResult.Value!));
    }

    [HttpPatch("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Patch(
        string id,
        [FromBody] UpdateResidentCredentialsResource resource,
        CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var existing = await userRepository.FindByExternalIdAsync(id, ct);
        if (existing is null || existing.Role != "resident")
            return NotFound(new { code = "RESIDENT_NOT_FOUND" });
        if (!BelongsToAdmin(existing, admin))
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN_TENANT" });

        var result = await userCommandService.Handle(
            new PatchResidentCommand(
                id,
                resource.Email,
                resource.Password,
                resource.BillFromNextMonth,
                resource.Name,
                resource.Floor,
                resource.Code),
            ct);

        if (result.IsFailure)
            return BadRequest(new { code = MapErrorCode(result.Error), message = result.Message });

        return Ok(UserResourceAssembler.ToResource(result.Value!));
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var existing = await userRepository.FindByExternalIdAsync(id, ct);
        if (existing is null || existing.Role != "resident")
            return NotFound(new { code = "RESIDENT_NOT_FOUND" });
        if (!BelongsToAdmin(existing, admin))
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN_TENANT" });

        var result = await userCommandService.Handle(new DeleteResidentCommand(id), ct);
        if (result.IsFailure)
            return NotFound(new { code = MapErrorCode(result.Error), message = result.Message });

        return NoContent();
    }

    /// <summary>
    /// DeactivateAccount (Tablas 53-54): solo residentes del condominio del admin. La cuenta queda
    /// DEACTIVATED: no inicia sesión y cada request autenticado recibe 403 ACCOUNT_DEACTIVATED.
    /// </summary>
    [HttpPatch("{id}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public Task<IActionResult> Deactivate(string id, CancellationToken ct) =>
        ChangeAccountStatusAsync(id, deactivate: true, ct);

    [HttpPatch("{id}/reactivate")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public Task<IActionResult> Reactivate(string id, CancellationToken ct) =>
        ChangeAccountStatusAsync(id, deactivate: false, ct);

    private async Task<IActionResult> ChangeAccountStatusAsync(string id, bool deactivate, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var existing = await userRepository.FindByExternalIdAsync(id, ct);
        if (existing is null || existing.Role != "resident")
            return NotFound(new { code = "RESIDENT_NOT_FOUND" });
        if (!BelongsToAdmin(existing, admin))
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN_TENANT" });

        var result = deactivate
            ? await userCommandService.Handle(new DeactivateAccountCommand(id), ct)
            : await userCommandService.Handle(new ReactivateAccountCommand(id), ct);
        if (result.IsFailure)
            return NotFound(new { code = MapErrorCode(result.Error), message = result.Message });

        return Ok(UserResourceAssembler.ToResource(result.Value!));
    }

    private IActionResult AdminFail() =>
        Unauthorized(new { code = "ADMIN_REQUIRED", message = "Solo administradores." });

    /// <summary>Cuenta del condominio activo, si quien llama lo gestiona (dueño, administrador asignado o junta).</summary>
    private async Task<Domain.Model.Aggregates.User?> RequireAdminAsync(CancellationToken ct)
    {
        var caller = await HttpContext.RequestServices
            .GetRequiredService<BuildingFex.Api.Iam.Application.Internal.Tenancy.ICallerContextAccessor>()
            .GetAsync(ct);
        return caller is not null && caller.IsAdmin ? caller.TenantAdmin : null;
    }

    private string? ResolveUid() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("user_id")
        ?? User.FindFirstValue("sub");

    private string? ResolveEmail() =>
        User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue("email");

    private static bool BelongsToAdmin(Domain.Model.Aggregates.User resident, Domain.Model.Aggregates.User admin) =>
        resident.OwnerAdminId == admin.Id
        || (resident.OwnerAdmin is not null
            && string.Equals(resident.OwnerAdmin.ExternalId, admin.ExternalId, StringComparison.Ordinal));

    private static string MapErrorCode(Enum? error) =>
        error switch
        {
            IamError.ResidentFieldsRequired => "RESIDENT_FIELDS_REQUIRED",
            IamError.ResidentCodeAlreadyExists => "RESIDENT_CODE_ALREADY_EXISTS",
            IamError.ResidentOwnerRequired => "RESIDENT_OWNER_REQUIRED",
            IamError.ResidentNotFound => "RESIDENT_NOT_FOUND",
            IamError.ResidentPlanLimitReached => "RESIDENT_PLAN_LIMIT_REACHED",
            IamError.SubscriptionTrialExpired => "SUBSCRIPTION_TRIAL_EXPIRED",
            IamError.SubscriptionInactive => "SUBSCRIPTION_INACTIVE",
            IamError.EmailAlreadyExists => "EMAIL_ALREADY_EXISTS",
            IamError.AdminBootstrapClosed => "ADMIN_BOOTSTRAP_CLOSED",
            IamError.InvalidIdToken => "INVALID_ID_TOKEN",
            IamError.InvalidEmail => "INVALID_EMAIL",
            IamError.AccountDeactivated => "ACCOUNT_DEACTIVATED",
            _ => "USER_ERROR",
        };
}
