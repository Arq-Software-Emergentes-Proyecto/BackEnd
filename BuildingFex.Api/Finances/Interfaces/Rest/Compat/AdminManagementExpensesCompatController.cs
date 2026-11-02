using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
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
[Route("adminManagementExpenses")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class AdminManagementExpensesCompatController(
    IAdminManagementExpenseRepository expenseRepository,
    IUnitOfWork unitOfWork) : TenantScopedControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? ownerAdminId, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;

        var rows = await expenseRepository.ListAsync(caller!.TenantExternalId, ct);
        return Ok(rows.Select(FinanceCompatSerializer.AdminExpenseToJson));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(BodyString(body, "ownerAdminId"), ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        var externalId = body.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? $"admin-exp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : $"admin-exp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        // US030 "Registro con monto inválido": 400 con el campo culpable (antes: 500 por GetProperty/GetDecimal).
        var name = BodyString(body, "name")?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return InvalidField("ADMIN_EXPENSE_NAME_REQUIRED", "name", "El nombre del gasto es obligatorio.");

        var amount = ReadAmount(body);
        if (amount is null || !Money.Of(amount.Value).IsPositive())
            return InvalidField("ADMIN_EXPENSE_AMOUNT_INVALID", "amount", "Ingresa un monto mayor a cero.");

        var purchaseDate = BodyString(body, "purchaseDate")?.Trim() ?? string.Empty;
        if (!MonthlyCharge.TryParseYmd(purchaseDate, out _))
            return InvalidField("ADMIN_EXPENSE_DATE_INVALID", "purchaseDate", "La fecha de compra debe tener formato yyyy-MM-dd.");

        var expense = AdminManagementExpense.Create(
            externalId,
            owner.Id,
            name,
            Money.Of(amount.Value).Amount,
            purchaseDate[..10],
            BodyString(body, "invoicePhotoUrl") ?? string.Empty);

        await expenseRepository.AddAsync(expense, ct);
        await unitOfWork.CompleteAsync(ct);

        var created = (await expenseRepository.ListAsync(owner.ExternalId, ct))
            .FirstOrDefault(e => e.ExternalId == externalId);

        return StatusCode(StatusCodes.Status201Created, FinanceCompatSerializer.AdminExpenseToJson(created!));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var expense = await expenseRepository.FindByExternalIdAsync(id, ct);
        if (expense is null)
            return NotFound();
        if (!BelongsToTenant(caller!, expense.OwnerAdminId))
            return ForbiddenTenant(caller!, "adminManagementExpense", id);

        expenseRepository.Remove(expense);
        await unitOfWork.CompleteAsync(ct);
        return NoContent();
    }

    private IActionResult InvalidField(string code, string field, string message) =>
        BadRequest(new
        {
            code,
            field,
            message,
            errors = new Dictionary<string, string[]> { [field] = [message] },
        });

    /// <summary>Monto numérico o string numérico; null si falta, está vacío o no es número.</summary>
    private static decimal? ReadAmount(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("amount", out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var number))
            return number;
        if (prop.ValueKind == JsonValueKind.String
            && decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
