using System.Security.Claims;
using BuildingFex.Api.Iam.Application.CommandServices;
using BuildingFex.Api.Iam.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Commands;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Profiles.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Profiles.Interfaces.Rest;

/// <summary>
/// Módulo Profiles — fuente única del perfil de negocio del admin.
/// Auth = Firebase idToken. Persistencia = tabla profiles (FK a users.external_id).
/// Role NUNCA del body: todo uid nuevo que completa su perfil queda como admin de un condominio
/// independiente (RF015, SaaS multi-tenant). Un uid que ya es residente no puede volverse admin.
/// POST asegura User + upsert Profile en UNA sola transacción.
/// </summary>
[ApiController]
[Route("api/v1/profiles")]
[Authorize]
public class ProfilesController(
    AppDbContext dbContext,
    IUserCommandService userCommandService,
    IUserRepository userRepository) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertProfileResource resource, CancellationToken ct)
    {
        var uid = ResolveUid();
        if (string.IsNullOrWhiteSpace(uid))
            return Unauthorized(new { code = "UNAUTHORIZED", message = "Token sin uid." });

        // Email siempre del token de Firebase (Google y email/contraseña lo traen); nunca del body.
        var email = ResolveEmail() ?? string.Empty;
        var name = string.IsNullOrWhiteSpace(resource.Name) ? "Admin" : resource.Name.Trim();

        await using var tx = await dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            // Role lo decide el servidor (UpsertFirebaseAdminCommand): nuevo uid → admin de su propio tenant.
            var ensureUser = await userCommandService.Handle(
                new UpsertFirebaseAdminCommand(
                    uid,
                    name,
                    email,
                    resource.Dni,
                    resource.Address,
                    resource.Company,
                    resource.Ruc),
                ct);

            if (ensureUser.IsFailure)
            {
                await tx.RollbackAsync(ct);
                if (ensureUser.Error is IamError.AdminBootstrapClosed)
                {
                    return Conflict(new
                    {
                        code = "ADMIN_BOOTSTRAP_CLOSED",
                        message = ensureUser.Message,
                    });
                }

                if (ensureUser.Error is IamError.EmailAlreadyExists)
                    return Conflict(new { code = "EMAIL_ALREADY_EXISTS", message = ensureUser.Message });
                if (ensureUser.Error is IamError.InvalidEmail)
                    return BadRequest(new { code = "INVALID_EMAIL", message = ensureUser.Message });

                return BadRequest(new { code = "PROFILE_USER_ERROR", message = ensureUser.Message });
            }

            var profile = await dbContext.Set<Profile>()
                .FirstOrDefaultAsync(p => p.UserExternalId == uid, ct);

            if (profile is null)
            {
                profile = Profile.Create(
                    uid,
                    name,
                    email,
                    resource.Dni,
                    resource.Address,
                    resource.Company,
                    resource.Ruc);
                dbContext.Set<Profile>().Add(profile);
            }
            else
            {
                profile.Update(name, email, resource.Dni, resource.Address, resource.Company, resource.Ruc);
            }

            // Espejo legacy en User para endpoints Compat que aún leen User.Dni/etc.
            var user = ensureUser.Value!;
            user.UpdateAdminProfile(name, resource.Dni, resource.Address, resource.Company, resource.Ruc);
            user.SetAccountKind(resource.AccountKind);
            userRepository.Update(user);

            await dbContext.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Ok(ToResource(profile));
        }
        catch (Exception ex) when (DbErrors.IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(ct);
            return Conflict(new { code = "EMAIL_ALREADY_EXISTS", message = "La cuenta ya está registrada." });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var uid = ResolveUid();
        if (string.IsNullOrWhiteSpace(uid))
            return Unauthorized(new { code = "UNAUTHORIZED", message = "Token sin uid." });

        var profile = await dbContext.Set<Profile>()
            .FirstOrDefaultAsync(p => p.UserExternalId == uid, ct);

        if (profile is null)
        {
            var legacy = await FindLegacyAdminResourceAsync(uid, ct);
            if (legacy is not null) return Ok(legacy);
            return NotFound(new { code = "PROFILE_NOT_FOUND", message = "Perfil no encontrado." });
        }

        return Ok(ToResource(profile));
    }

    [HttpGet("{userId}")]
    public async Task<IActionResult> GetByUserId(string userId, CancellationToken ct)
    {
        var cleanId = (userId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(cleanId))
            return NotFound(new { code = "PROFILE_NOT_FOUND", message = "Perfil no encontrado." });

        // Solo el propio perfil o el del admin de tu condominio (antes: cualquier perfil por id).
        var uid = ResolveUid();
        if (!string.Equals(uid, cleanId, StringComparison.Ordinal))
        {
            var caller = await HttpContext.RequestServices
                .GetRequiredService<BuildingFex.Api.Iam.Application.Internal.Tenancy.ICallerContextAccessor>()
                .GetAsync(ct);
            if (caller is null || !caller.OwnsTenant(cleanId))
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    code = "FORBIDDEN_TENANT",
                    message = "No tienes acceso a este perfil.",
                });
        }

        var profile = await dbContext.Set<Profile>()
            .FirstOrDefaultAsync(p => p.UserExternalId == cleanId, ct);

        if (profile is null)
        {
            var legacy = await FindLegacyAdminResourceAsync(cleanId, ct);
            if (legacy is not null) return Ok(legacy);
            return NotFound(new { code = "PROFILE_NOT_FOUND", message = "Perfil no encontrado." });
        }

        return Ok(ToResource(profile));
    }

    /// <summary>
    /// Admins provisioned before the profiles table (db.json seed, password bootstrap) have
    /// their business data on the users row only: serve it instead of a 404 so Settings and
    /// login do not treat an existing condominium admin as an incomplete registration.
    /// </summary>
    private async Task<object?> FindLegacyAdminResourceAsync(string externalId, CancellationToken ct)
    {
        var user = await userRepository.FindByExternalIdAsync(externalId, ct);
        if (user is null || user.Role != "admin") return null;
        return new
        {
            id = user.ExternalId,
            name = user.Name,
            email = user.Email ?? string.Empty,
            role = "admin",
            dni = user.Dni ?? string.Empty,
            address = user.Address ?? string.Empty,
            company = user.Company ?? string.Empty,
            ruc = user.Ruc ?? string.Empty,
        };
    }

    private string? ResolveUid() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("user_id")
        ?? User.FindFirstValue("sub");

    private string? ResolveEmail() =>
        User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue("email");

    private static object ToResource(Profile profile) => new
    {
        id = profile.UserExternalId,
        name = profile.Name,
        email = profile.Email,
        role = "admin",
        dni = profile.Dni ?? string.Empty,
        address = profile.Address ?? string.Empty,
        company = profile.Company ?? string.Empty,
        ruc = profile.Ruc ?? string.Empty,
    };
}

public class UpsertProfileResource
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Dni { get; set; }
    public string? Address { get; set; }
    public string? Company { get; set; }
    public string? Ruc { get; set; }

    /// <summary>administrator | board: quién registra la cuenta (solo se fija al crearla).</summary>
    public string? AccountKind { get; set; }
}
