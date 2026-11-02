using BuildingFex.Api.Finances.Application.Internal.Billing;
using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
using BuildingFex.Api.Finances.Domain.Repositories;
using BuildingFex.Api.Finances.Interfaces.Rest.Transform;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace BuildingFex.Api.Finances.Interfaces.Rest.Compat;

[ApiController]
[Route("receipts")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class ReceiptsCompatController(
    IReceiptRepository receiptRepository,
    IUnitOfWork unitOfWork) : TenantScopedControllerBase
{
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

        var receipts = await receiptRepository.ListAsync(caller!.TenantExternalId, effectiveResidentId, ct);
        return Ok(receipts.Select(FinanceCompatSerializer.ReceiptToJson));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(BodyString(body, "ownerAdminId"), ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        // Id por defecto: Guid (no ms de reloj, que choca entre condominios / clics simultáneos).
        var externalId = body.TryGetProperty("id", out var idProp)
            ? FinanceCompatSerializer.NormalizeExternalId(idProp)
            : MonthlyBillingService.NewReceiptExternalId();

        var residentId = BodyString(body, "residentId")?.Trim() ?? string.Empty;
        if (residentId.Length == 0)
            return BadRequest(new { code = "RECEIPT_RESIDENT_REQUIRED", field = "residentId", message = "residentId es obligatorio." });

        var billingMonth = Period.Normalize(BodyString(body, "billingMonth"));
        var amount = body.TryGetProperty("amount", out var amountProp) ? amountProp.GetDecimal() : 0;
        var extraCharges = body.TryGetProperty("extraCharges", out var extraProp) ? extraProp.GetDecimal() : 0;
        var kind = ReceiptKind.Resolve(BodyString(body, "kind"), amount, extraCharges);

        // RNF011: una cuota MENSUAL por residente y mes. Si ya existe (doble clic, worker nocturno, otra pestaña)
        // se devuelve la existente en vez de duplicarla. Los cargos extra (kind EXTRA) siempre se crean.
        if (billingMonth is not null && kind == ReceiptKind.Monthly)
        {
            var duplicate = await FindMonthlyReceiptAsync(owner.Id, residentId, billingMonth, ct);
            if (duplicate is not null)
                return Ok(FinanceCompatSerializer.ReceiptToJson(duplicate));
        }

        var receipt = Receipt.Create(
            externalId,
            owner.Id,
            residentId,
            body.TryGetProperty("issueDate", out var issueProp) ? issueProp.GetString() : null,
            body.TryGetProperty("dueDate", out var dueProp) ? dueProp.GetString() : null,
            amount,
            body.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "Pending" : "Pending",
            body.TryGetProperty("lateFee", out var lateProp) ? lateProp.GetDecimal() : 0,
            extraCharges,
            body.TryGetProperty("concept", out var conceptProp) ? conceptProp.GetString() : null,
            billingMonth,
            BodyString(body, "paymentOpen"),
            kind);

        await receiptRepository.AddAsync(receipt, ct);
        try
        {
            await unitOfWork.CompleteAsync(ct);
        }
        catch (DbUpdateException ex) when (billingMonth is not null
                                           && kind == ReceiptKind.Monthly
                                           && DbErrors.IsDuplicateKey(ex, ReceiptIndexes.UniqueMonthly))
        {
            // Carrera con la facturación automática: el índice único (owner, residente, monthly_key) ganó.
            var existing = await FindMonthlyReceiptAsync(owner.Id, residentId, billingMonth, ct);
            if (existing is not null)
                return Ok(FinanceCompatSerializer.ReceiptToJson(existing));
            throw;
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            return Conflict(new { code = "RECEIPT_ID_TAKEN", field = "id", message = "Ya existe un recibo con ese id." });
        }

        var created = await receiptRepository.FindByExternalIdAsync(externalId, ct);
        return StatusCode(StatusCodes.Status201Created, FinanceCompatSerializer.ReceiptToJson(created!));
    }

    [HttpPatch("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Patch(string id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var receipt = await receiptRepository.FindByExternalIdAsync(id, ct);
        if (receipt is null)
            return NotFound();
        if (!BelongsToTenant(caller!, receipt.OwnerAdminId))
            return ForbiddenTenant(caller!, "receipt", id);

        string? status = body.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
        decimal? lateFee = body.TryGetProperty("lateFee", out var lateProp)
            ? lateProp.GetDecimal()
            : null;
        decimal? extraCharges = body.TryGetProperty("extraCharges", out var extraProp)
            ? extraProp.GetDecimal()
            : null;
        string? concept = body.TryGetProperty("concept", out var conceptProp) ? conceptProp.GetString() : null;

        receipt.Patch(status, lateFee, extraCharges, concept);
        receiptRepository.Update(receipt);
        await unitOfWork.CompleteAsync(ct);

        var updated = await receiptRepository.FindByExternalIdAsync(id, ct);
        return Ok(FinanceCompatSerializer.ReceiptToJson(updated!));
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var receipt = await receiptRepository.FindByExternalIdAsync(id, ct);
        if (receipt is null)
            return NotFound();
        if (!BelongsToTenant(caller!, receipt.OwnerAdminId))
            return ForbiddenTenant(caller!, "receipt", id);

        receiptRepository.Remove(receipt);
        await unitOfWork.CompleteAsync(ct);
        return NoContent();
    }

    private async Task<Receipt?> FindMonthlyReceiptAsync(
        int ownerAdminId,
        string residentId,
        string billingMonth,
        CancellationToken ct) =>
        (await receiptRepository.ListAsync(null, residentId, ct))
            .FirstOrDefault(r => r.OwnerAdminId == ownerAdminId && r.IsMonthly && r.BillingMonth == billingMonth);
}
