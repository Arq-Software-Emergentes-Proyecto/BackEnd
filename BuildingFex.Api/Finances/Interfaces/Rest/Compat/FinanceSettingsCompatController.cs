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
[Route("financeSettings")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class FinanceSettingsCompatController(
    IFinanceSettingRepository settingRepository,
    IUnitOfWork unitOfWork) : TenantScopedControllerBase
{
    /// <summary>Lectura también para residentes (reglas de mora / fechas de pago de su condominio).</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? ownerAdminId, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;

        var settings = await settingRepository.ListAsync(caller!.TenantExternalId, ct);
        return Ok(settings.Select(FinanceCompatSerializer.FinanceSettingToJson));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(BodyString(body, "ownerAdminId"), ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        var externalId = body.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? $"finance-settings-{owner.ExternalId}"
            : $"finance-settings-{owner.ExternalId}";

        var setting = FinanceSetting.Create(
            externalId,
            owner.Id,
            body.TryGetProperty("baseMonthlyExpense", out var baseProp) ? baseProp.GetDecimal() : 150,
            body.TryGetProperty("lateFeeRate", out var rateProp) ? rateProp.GetDecimal() : 0.05m);
        ApplyBillingRules(setting, body);

        await settingRepository.AddAsync(setting, ct);
        await unitOfWork.CompleteAsync(ct);

        var created = await settingRepository.FindByExternalIdAsync(externalId, ct);
        return StatusCode(StatusCodes.Status201Created, FinanceCompatSerializer.FinanceSettingToJson(created!));
    }

    [HttpPatch]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Update([FromQuery] string? ownerAdminId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(ownerAdminId, ct);
        if (error is not null) return error;
        var owner = caller!.TenantAdmin;

        decimal? baseMonthlyExpense = body.TryGetProperty("baseMonthlyExpense", out var baseProp)
            ? baseProp.GetDecimal()
            : null;
        decimal? lateFeeRate = body.TryGetProperty("lateFeeRate", out var rateProp)
            ? rateProp.GetDecimal()
            : null;

        var settings = await settingRepository.ListAsync(owner.ExternalId, ct);
        var setting = settings.FirstOrDefault();
        var externalId = $"finance-settings-{owner.ExternalId}";

        if (setting is null)
        {
            setting = FinanceSetting.Create(
                externalId,
                owner.Id,
                baseMonthlyExpense ?? 150,
                lateFeeRate ?? 0.05m);
            ApplyBillingRules(setting, body);
            await settingRepository.AddAsync(setting, ct);
        }
        else
        {
            setting.Patch(baseMonthlyExpense, lateFeeRate);
            ApplyBillingRules(setting, body);
            settingRepository.Update(setting);
        }

        await unitOfWork.CompleteAsync(ct);

        var saved = await settingRepository.FindByExternalIdAsync(setting.ExternalId, ct);
        return Ok(FinanceCompatSerializer.FinanceSettingToJson(saved ?? setting));
    }

    /// <summary>
    /// Reglas de facturación que antes se perdían en el backend: recargo diario, rango de admisión
    /// y las cuotas mensuales (base de la facturación automática RF021).
    /// </summary>
    private static void ApplyBillingRules(FinanceSetting setting, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return;

        setting.PatchBillingRules(
            ReadDecimal(body, "lateFeePerDay"),
            ReadInt(body, "admissionPayFromDay"),
            ReadInt(body, "admissionPayToDay"),
            body.TryGetProperty("monthlyCharges", out var charges) && charges.ValueKind == JsonValueKind.Array
                ? MonthlyCharge.ParseList(charges)
                : null);
        setting.SetReservationRestriction(ReadBool(body, "restrictReservationsWhenDelinquent"));
    }

    private static bool? ReadBool(JsonElement body, string name) =>
        body.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean()
            : null;

    private static decimal? ReadDecimal(JsonElement body, string name) =>
        body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var v)
            ? v
            : null;

    private static int? ReadInt(JsonElement body, string name) =>
        body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)
            ? v
            : null;
}
