using System.Security.Claims;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Communications.Interfaces.Rest;

/// <summary>Carpetas de Import — solo administradores.</summary>
[ApiController]
[Route("api/v1/import/folders")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class ImportFoldersController(
    AppDbContext db,
    IUserRepository users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var rows = await db.Set<ImportFolder>()
            .Where(f => f.OwnerAdminId == admin)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);
        return Ok(rows.Select(Map));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateFolderBody body, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var folder = ImportFolder.Create(
            body.Id ?? $"folder-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            admin,
            body.Name ?? "Carpeta",
            body.IsHome ?? false);

        db.Set<ImportFolder>().Add(folder);
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, Map(folder));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] PatchFolderBody body, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var folder = await FindFolderAsync(id, admin, ct);
        if (folder is null) return NotFound();

        if (folder.IsHome)
            folder.Patch("Home", true);
        else
            folder.Patch(body.Name, body.IsHome);

        await db.SaveChangesAsync(ct);
        return Ok(Map(folder));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var folder = await FindFolderAsync(id, admin, ct);
        if (folder is null) return NotFound();
        if (folder.IsHome)
            return BadRequest(new { code = "HOME_FOLDER_PROTECTED", message = "No se puede eliminar Home." });

        db.Set<ImportFolder>().Remove(folder);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IActionResult AdminFail() =>
        Unauthorized(new { code = "ADMIN_REQUIRED", message = "Import solo para administradores." });

    /// <summary>ownerAdminId del condominio activo, si quien llama lo gestiona (dueño, administrador asignado o junta).</summary>
    private async Task<string?> RequireAdminAsync(CancellationToken ct)
    {
        var caller = await HttpContext.RequestServices
            .GetRequiredService<BuildingFex.Api.Iam.Application.Internal.Tenancy.ICallerContextAccessor>()
            .GetAsync(ct);
        return caller is not null && caller.IsAdmin ? caller.TenantExternalId : null;
    }

    private async Task<ImportFolder?> FindFolderAsync(string id, string owner, CancellationToken ct)
    {
        var clean = (id ?? string.Empty).Trim();
        return await db.Set<ImportFolder>()
            .FirstOrDefaultAsync(
                f => f.OwnerAdminId == owner && (f.ExternalId == clean || f.Id.ToString() == clean),
                ct);
    }

    private static object Map(ImportFolder f) => new
    {
        id = f.ExternalId,
        name = f.Name,
        isHome = f.IsHome,
        createdAt = f.CreatedAt.ToString("o"),
        ownerAdminId = f.OwnerAdminId,
    };
}

public class CreateFolderBody
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool? IsHome { get; set; }
}

public class PatchFolderBody
{
    public string? Name { get; set; }
    public bool? IsHome { get; set; }
}
