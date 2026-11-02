using BuildingFex.Api.Finances.Domain.Repositories;
using BuildingFex.Api.Finances.Interfaces.Rest.Transform;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BuildingFex.Api.Finances.Interfaces.Rest.Compat;

[ApiController]
[Route("fees")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class FeesCompatController(
    IFeeRepository feeRepository,
    IUnitOfWork unitOfWork) : TenantScopedControllerBase
{
    /// <summary>Admin: cuotas de su condominio. Residente: solo las suyas.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? ownerAdminId,
        [FromQuery] string? residentId,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;
        var scopeError = ResolveResidentScope(caller!, residentId, out var effectiveResidentId);
        if (scopeError is not null) return scopeError;

        var fees = await feeRepository.ListAsync(caller!.TenantExternalId, effectiveResidentId, ct);
        return Ok(fees.Select(FinanceCompatSerializer.FeeToJson));
    }

    /// <summary>Cambiar estado (p.ej. marcar pagada) es solo del admin; el residente paga vía Mercado Pago.</summary>
    [HttpPatch("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Patch(string id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var fee = await feeRepository.FindByExternalIdAsync(id, ct);
        if (fee is null)
            return NotFound();
        if (!BelongsToTenant(caller!, fee.OwnerAdminId))
            return ForbiddenTenant(caller!, "fee", id);

        if (body.TryGetProperty("status", out var statusElement))
            fee.UpdateStatus(statusElement.GetString() ?? fee.Status);

        feeRepository.Update(fee);
        await unitOfWork.CompleteAsync(ct);

        var updated = await feeRepository.FindByExternalIdAsync(id, ct);
        return Ok(FinanceCompatSerializer.FeeToJson(updated!));
    }
}
