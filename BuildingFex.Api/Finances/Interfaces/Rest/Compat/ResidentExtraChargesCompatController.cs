using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Finances.Interfaces.Rest.Compat;

[ApiController]
[Route("residentExtraCharges")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class ResidentExtraChargesCompatController(AppDbContext dbContext) : TenantScopedControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? ownerAdminId, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;
        var tenant = caller!.TenantExternalId;

        var query = dbContext.Set<ResidentExtraCharge>().Where(c => c.OwnerAdminId == tenant);
        var rows = await query.OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(Map));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateResidentExtraChargeResource resource, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(resource.OwnerAdminId, ct);
        if (error is not null) return error;

        var charge = ResidentExtraCharge.Create(
            resource.Id ?? $"extra-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            caller!.TenantExternalId,
            resource.ResidentId ?? string.Empty,
            resource.Title ?? string.Empty,
            resource.Amount,
            resource.Description ?? string.Empty);

        dbContext.Set<ResidentExtraCharge>().Add(charge);
        await dbContext.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, Map(charge));
    }

    private static object Map(ResidentExtraCharge c) => new
    {
        id = c.ExternalId,
        ownerAdminId = c.OwnerAdminId,
        residentId = c.ResidentId,
        title = c.Title,
        amount = c.Amount,
        description = c.Description,
        createdAt = c.CreatedAt.ToString("o"),
    };
}

public class CreateResidentExtraChargeResource
{
    public string? Id { get; set; }
    public string? OwnerAdminId { get; set; }
    public string? ResidentId { get; set; }
    public string? Title { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}
