using BuildingFex.Api.Finances.Domain.Model.Aggregates;
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
[Route("payments")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class PaymentsCompatController(
    IPaymentRepository paymentRepository,
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

        var payments = await paymentRepository.ListAsync(caller!.TenantExternalId, effectiveResidentId, ct);
        return Ok(payments.Select(FinanceCompatSerializer.PaymentToJson));
    }

    /// <summary>
    /// Registro manual de pagos (efectivo/transferencia) — solo admin.
    /// Los residentes pagan exclusivamente vía Mercado Pago (/api/v1/payments/checkout + webhook/confirm).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(BodyString(body, "ownerAdminId"), ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        var externalId = body.TryGetProperty("id", out var idProp)
            ? FinanceCompatSerializer.NormalizeExternalId(idProp, "MP-")
            : $"MP-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var feeId = body.TryGetProperty("feeId", out var feeProp)
            ? FinanceCompatSerializer.NormalizeOptionalExternalId(feeProp)
            : null;

        var payment = Payment.Create(
            externalId,
            owner.Id,
            body.GetProperty("residentId").GetString() ?? string.Empty,
            body.TryGetProperty("amount", out var amountProp) ? amountProp.GetDecimal() : 0,
            feeId,
            body.TryGetProperty("feeMonth", out var monthProp) ? monthProp.GetString() : null,
            body.TryGetProperty("paidAt", out var paidProp) ? paidProp.GetString() : null,
            body.TryGetProperty("method", out var methodProp) ? methodProp.GetString() : null,
            body.TryGetProperty("reference", out var refProp) ? refProp.GetString() : null);

        await paymentRepository.AddAsync(payment, ct);
        await unitOfWork.CompleteAsync(ct);

        var created = await paymentRepository.FindByExternalIdAsync(externalId, ct);
        return StatusCode(StatusCodes.Status201Created, FinanceCompatSerializer.PaymentToJson(created!));
    }
}
