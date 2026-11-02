using BuildingFex.Api.Iam.Application.CommandServices;
using BuildingFex.Api.Iam.Application.Internal.OutboundServices;
using BuildingFex.Api.Iam.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Commands;
using BuildingFex.Api.Iam.Application.QueryServices;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Iam.Interfaces.Rest.Resources;
using BuildingFex.Api.Iam.Interfaces.Rest.Transform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Shared.Infrastructure.RateLimiting;

namespace BuildingFex.Api.Iam.Interfaces.Rest;

/// <summary>
/// Auth: Firebase idToken en todos lados. Residentes completan invite → custom claims (sin App JWT).
/// </summary>
[ApiController]
[Route("api/v1/authentication")]
public class AuthenticationController(
    IUserCommandService userCommandService,
    IUserRepository userRepository,
    ICallerContextAccessor callerContextAccessor) : ControllerBase
{
    /// <summary>
    /// Login multi-rol (US014): tras autenticarse en Firebase (email/contraseña o Google) el cliente
    /// consulta su usuario y rol para redirigir al dashboard de admin o de residente.
    /// 404 USER_NOT_PROVISIONED si el uid no está en users (admin que aún no completó su perfil).
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var caller = await callerContextAccessor.GetAsync(ct);
        if (caller is null)
            return NotFound(new { code = "USER_NOT_PROVISIONED", message = "Usuario sin perfil en BuildingFex." });

        return Ok(FirebaseAuthResponse(caller.User));
    }

    [HttpPost("sign-in")]
    [AllowAnonymous]
    public async Task<IActionResult> SignIn([FromBody] SignInResource resource, CancellationToken ct)
    {
        var result = await userCommandService.Handle(new SignInCommand(resource.Email, resource.Password), ct);
        if (result.IsFailure)
            return MapAuthFailure(result.Error, result.Message);

        return Ok(FirebaseAuthResponse(result.Value!.user));
    }

    [HttpPost("sign-in-google")]
    [AllowAnonymous]
    public async Task<IActionResult> SignInGoogle([FromBody] SignInGoogleResource resource, CancellationToken ct)
    {
        var result = await userCommandService.Handle(
            new SignInGoogleCommand(
                resource.Email ?? string.Empty,
                resource.GoogleSub ?? string.Empty,
                resource.IdToken),
            ct);

        if (result.IsFailure)
            return MapAuthFailure(result.Error, result.Message);

        return Ok(FirebaseAuthResponse(result.Value!.user));
    }

    [HttpPost("register-admin")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterAdmin([FromBody] RegisterAdminResource resource, CancellationToken ct)
    {
        var result = await userCommandService.Handle(
            new RegisterAdminCommand(
                resource.Name,
                resource.Email,
                resource.Password,
                resource.Dni,
                resource.Address,
                resource.Company,
                resource.Ruc),
            ct);

        if (result.IsFailure)
            return MapAuthFailure(result.Error, result.Message);

        return Ok(FirebaseAuthResponse(result.Value!.user));
    }

    /// <summary>Lookup por invite token (128-bit). DTO mínimo.</summary>
    [HttpGet("residents/invite")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthLookup)]
    public async Task<IActionResult> GetResidentInvite(
        [FromQuery] string? token,
        [FromQuery] string? code,
        CancellationToken ct)
    {
        // `token` preferido; `code` legacy alias → también busca InviteToken
        var inviteKey = !string.IsNullOrWhiteSpace(token) ? token.Trim() : (code ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(inviteKey))
            return NotFound(new { code = "RESIDENT_NOT_FOUND", message = "Invitación no encontrada." });

        var resident = await userRepository.FindByInviteTokenAsync(inviteKey, ct);
        if (resident is null || resident.Role != "resident")
            return NotFound(new { code = "RESIDENT_NOT_FOUND", message = "Invitación no encontrada." });

        if (resident.InviteUsedAt.HasValue)
            return BadRequest(new { code = "INVITE_ALREADY_USED", message = "Esta invitación ya fue usada." });

        if (resident.InviteExpiresAt.HasValue && resident.InviteExpiresAt.Value < DateTimeOffset.UtcNow)
            return BadRequest(new { code = "INVITE_EXPIRED", message = "Esta invitación expiró." });

        return Ok(new
        {
            name = resident.Name,
            floor = resident.Floor,
            code = resident.Code,
            inviteExpiresAt = resident.InviteExpiresAt?.ToString("o"),
        });
    }

    /// <summary>
    /// Completa la invitación con un idToken Firebase de Google o de email/contraseña.
    /// Alias: residents/complete-invite.
    /// </summary>
    [HttpPost("residents/complete-google")]
    [HttpPost("residents/complete-invite")]
    [AllowAnonymous]
    public async Task<IActionResult> CompleteResidentGoogle(
        [FromBody] CompleteResidentGoogleResource resource,
        CancellationToken ct)
    {
        var inviteKey = !string.IsNullOrWhiteSpace(resource.Token)
            ? resource.Token.Trim()
            : (resource.Code ?? string.Empty).Trim();

        var result = await userCommandService.Handle(
            new CompleteResidentGoogleCommand(
                inviteKey,
                resource.Email ?? string.Empty,
                resource.GoogleSub ?? string.Empty,
                resource.Name,
                resource.IdToken),
            ct);

        if (result.IsFailure)
        {
            if (result.Error is IamError.ResidentNotFound)
                return NotFound(new { code = MapErrorCode(result.Error), message = result.Message });
            return MapAuthFailure(result.Error, result.Message);
        }

        return Ok(FirebaseAuthResponse(result.Value!.user));
    }

    [HttpPost("residents/set-credentials")]
    [AllowAnonymous]
    public IActionResult SetResidentCredentials([FromBody] SetResidentCredentialsByCodeResource resource)
    {
        return StatusCode(StatusCodes.Status410Gone, new
        {
            code = "USE_COMPLETE_GOOGLE",
            message = "Usa complete-google con idToken de Firebase. set-credentials fue retirado.",
        });
    }

    private static object FirebaseAuthResponse(Domain.Model.Aggregates.User user) =>
        new AuthenticatedUserResource(
            UserResourceAssembler.ToResource(user),
            Token: null,
            UseFirebaseIdToken: true);

    private IActionResult MapAuthFailure(Enum? error, string? message)
    {
        var code = MapErrorCode(error);
        var status = error switch
        {
            IamError.EmailAlreadyExists => StatusCodes.Status409Conflict,
            IamError.AdminBootstrapClosed => StatusCodes.Status409Conflict,
            IamError.ResidentNotFound => StatusCodes.Status404NotFound,
            IamError.UseFirebaseAuth => StatusCodes.Status410Gone,
            IamError.AccountDeactivated => StatusCodes.Status403Forbidden,
            IamError.EmailNotFound or IamError.InvalidPassword or IamError.ResidentOnly
                or IamError.ResidentGoogleRequired or IamError.ResidentUseGoogle
                or IamError.InvalidCredentials or IamError.InvalidIdToken => StatusCodes.Status401Unauthorized,
            IamError.InviteExpired or IamError.InviteAlreadyUsed or IamError.InviteEmailMismatch
                or IamError.InviteEmailUnverified => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest,
        };
        return StatusCode(status, new { code, message });
    }

    private static string MapErrorCode(Enum? error) =>
        error switch
        {
            IamError.EmailNotFound => "EMAIL_NOT_FOUND",
            IamError.InvalidPassword => "INVALID_PASSWORD",
            IamError.EmailAlreadyExists => "EMAIL_ALREADY_EXISTS",
            IamError.InvalidCredentials => "INVALID_PASSWORD",
            IamError.ResidentNotFound => "RESIDENT_NOT_FOUND",
            IamError.ResidentFieldsRequired => "RESIDENT_FIELDS_REQUIRED",
            IamError.ResidentOnly => "RESIDENT_ONLY",
            IamError.ResidentGoogleRequired => "RESIDENT_GOOGLE_REQUIRED",
            IamError.ResidentUseGoogle => "RESIDENT_USE_GOOGLE",
            IamError.AdminBootstrapClosed => "ADMIN_BOOTSTRAP_CLOSED",
            IamError.InvalidIdToken => "INVALID_ID_TOKEN",
            IamError.InviteExpired => "INVITE_EXPIRED",
            IamError.InviteAlreadyUsed => "INVITE_ALREADY_USED",
            IamError.InviteEmailMismatch => "INVITE_EMAIL_MISMATCH",
            IamError.InviteEmailUnverified => "INVITE_EMAIL_UNVERIFIED",
            IamError.UseFirebaseAuth => "USE_FIREBASE_AUTH",
            IamError.AccountDeactivated => "ACCOUNT_DEACTIVATED",
            IamError.InvalidEmail => "INVALID_EMAIL",
            _ => "AUTH_ERROR",
        };
}
