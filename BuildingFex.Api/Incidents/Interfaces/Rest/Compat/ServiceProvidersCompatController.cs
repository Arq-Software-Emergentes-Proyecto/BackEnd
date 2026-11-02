using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Interfaces.Rest;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Incidents.Interfaces.Rest.Compat;

[ApiController]
[Route("serviceProviders")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class ServiceProvidersCompatController(AppDbContext dbContext) : TenantScopedControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? ownerAdminId, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;
        var tenant = caller!.TenantExternalId;

        var query = dbContext.Set<BuildingServiceProvider>().Where(p => p.OwnerAdminId == tenant);
        var rows = await query.OrderBy(p => p.Name).ToListAsync(ct);
        return Ok(rows.Select(Map));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Create([FromBody] CreateServiceProviderResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(resource.OwnerAdminId, ct);
        if (error is not null) return error;

        var provider = BuildingServiceProvider.Create(
            resource.Id ?? $"provider-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            caller!.TenantExternalId,
            resource.Name ?? string.Empty,
            resource.Phone ?? string.Empty,
            resource.Specialty ?? string.Empty);

        dbContext.Set<BuildingServiceProvider>().Add(provider);
        await dbContext.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, Map(provider));
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var provider = await dbContext.Set<BuildingServiceProvider>()
            .FirstOrDefaultAsync(p => p.ExternalId == id || p.Id.ToString() == id, ct);
        if (provider is null) return NotFound();
        if (!BelongsToTenant(caller!, provider.OwnerAdminId))
            return ForbiddenTenant(caller!, "serviceProvider", id);
        dbContext.Set<BuildingServiceProvider>().Remove(provider);
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    private static object Map(BuildingServiceProvider p) => new
    {
        id = p.ExternalId,
        name = p.Name,
        phone = p.Phone,
        specialty = p.Specialty,
        ownerAdminId = p.OwnerAdminId,
    };
}

public class CreateServiceProviderResource
{
    public string? Id { get; set; }
    public string? OwnerAdminId { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Specialty { get; set; }
}
