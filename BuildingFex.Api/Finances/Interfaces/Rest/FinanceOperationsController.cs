using System.Text.Json;
using BuildingFex.Api.Finances.Application.Internal.Billing;
using BuildingFex.Api.Finances.Application.Internal.Dashboard;
using BuildingFex.Api.Finances.Application.Internal.Delinquency;
using BuildingFex.Api.Finances.Application.Internal.ReceiptDocuments;
using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
using BuildingFex.Api.Finances.Domain.Repositories;
using BuildingFex.Api.Finances.Domain.Services;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Profiles.Domain.Repositories;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Finances.Interfaces.Rest;

/// <summary>
/// Casos de uso de Finanzas sobre el contrato v1: facturación mensual (RF021 / TS003),
/// semáforo de deudas (RF029), comprobante descargable (RF022) y balance neto mensual (RF030).
/// El tenant sale siempre del token (TenantScopedControllerBase); los residentes solo ven lo suyo.
/// </summary>
[ApiController]
[Route("api/v1/finances")]
[Tags("Finances")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class FinanceOperationsController(
    IMonthlyBillingService billingService,
    IPaymentCalendarQueryService calendarQueryService,
    IReceiptDocumentService receiptDocumentService,
    IDashboardQueryService dashboardQueryService,
    IPaymentRepository paymentRepository,
    IReceiptRepository receiptRepository,
    IBuildingRepository buildingRepository) : TenantScopedControllerBase
{
    /// <summary>
    /// TS003: genera (idempotente) los recibos Pending del mes para el condominio del admin.
    /// month opcional "yyyy-MM" (por defecto el mes actual, hora Lima). 200 con la cantidad creada;
    /// 422 MISSING_MONTHLY_CHARGE si el mes no tiene cuota configurada (no se emite nada).
    /// </summary>
    [HttpPost("receipts/generate")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GenerateReceipts(
        [FromQuery] string? month,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonElement? body,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(BodyOrNull(body, "ownerAdminId"), ct);
        if (error is not null) return error;

        var result = await billingService.GenerateForTenantAsync(
            caller!.TenantId, month ?? BodyOrNull(body, "month"), ct);
        return BillingResult(result);
    }

    /// <summary>Alias literal de TS003: POST /api/v1/buildings/{buildingId}/receipts/generate (building = condominio del admin).</summary>
    [HttpPost("~/api/v1/buildings/{buildingId}/receipts/generate")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GenerateBuildingReceipts(
        string buildingId,
        [FromQuery] string? month,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonElement? body,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;
        // buildingId = id del edificio registrado (Profiles) o, por compatibilidad, el id del admin dueño.
        var ownsBuilding = caller!.OwnsTenant(buildingId)
                           || string.Equals(buildingId, caller.TenantId.ToString(), StringComparison.Ordinal);
        if (!ownsBuilding && int.TryParse(buildingId, out var buildingPk))
        {
            var buildings = await buildingRepository.ListByOwnerAdminIdAsync(caller.TenantId, ct);
            ownsBuilding = buildings.Any(b => b.Id == buildingPk);
        }
        if (!ownsBuilding)
            return ForbiddenTenant(caller, "buildingId", buildingId);

        var result = await billingService.GenerateForTenantAsync(
            caller.TenantId, month ?? BodyOrNull(body, "month"), ct);
        return BillingResult(result);
    }

    /// <summary>US021: alertas de facturación (p. ej. falta la cuota del mes → emisión detenida).</summary>
    [HttpGet("billing-alerts")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetBillingAlerts(CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var alerts = await billingService.GetAlertsAsync(caller!.TenantId, ct);
        return Ok(alerts.Select(a => new { code = a.Code, month = a.Month, message = a.Message }));
    }

    /// <summary>
    /// RF029: calendario semaforizado residente × mes (por defecto últimos 6 meses).
    /// Admin: todos los residentes (o filtra residentId). Residente: solo su fila.
    /// </summary>
    [HttpGet("payment-calendar")]
    public async Task<IActionResult> GetPaymentCalendar(
        [FromQuery] string? residentId,
        [FromQuery] int? months,
        CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;
        var scopeError = ResolveResidentScope(caller!, residentId, out var effectiveResidentId);
        if (scopeError is not null) return scopeError;

        var calendar = await calendarQueryService.GetAsync(caller!.TenantId, effectiveResidentId, months ?? 6, ct);
        return Ok(new
        {
            today = calendar.Today,
            dueSoonDays = calendar.DueSoonDays,
            months = calendar.Months,
            residents = calendar.Residents.Select(r => new
            {
                residentId = r.ResidentId,
                name = r.Name,
                floor = r.Floor,
                code = r.Code,
                overdueAmount = r.OverdueAmount,
                hasOverdue = r.HasOverdue,
                cells = r.Cells.Select(c => new
                {
                    month = c.Month,
                    status = c.Status,
                    amount = c.Amount,
                    dueDate = c.DueDate,
                    daysOverdue = c.DaysOverdue,
                    receiptIds = c.ReceiptIds,
                }),
            }),
        });
    }

    /// <summary>RF022: comprobante PDF de un pago aprobado. Residente: solo sus pagos; admin: los de su condominio.</summary>
    [HttpGet("payments/{paymentId}/receipt-pdf")]
    public async Task<IActionResult> DownloadPaymentReceipt(string paymentId, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var payment = await paymentRepository.FindByExternalIdAsync(paymentId, ct);
        if (payment is null)
            return NotFound(new { code = "PAYMENT_NOT_FOUND", message = "Pago no encontrado." });
        if (!BelongsToTenant(caller!, payment.OwnerAdminId))
            return ForbiddenTenant(caller!, "payment", paymentId);
        if (!caller!.IsAdmin && !string.Equals(payment.ResidentExternalId, caller.UserExternalId, StringComparison.Ordinal))
            return ForbiddenTenant(caller, "payment", paymentId);

        var doc = await receiptDocumentService.ForPaymentAsync(payment, ct);
        return File(doc.Content, "application/pdf", doc.FileName);
    }

    /// <summary>RF022: comprobante PDF de un recibo ya pagado (409 RECEIPT_NOT_PAID si sigue pendiente).</summary>
    [HttpGet("receipts/{receiptId}/pdf")]
    public async Task<IActionResult> DownloadReceipt(string receiptId, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        var receipt = await receiptRepository.FindByExternalIdAsync(receiptId, ct);
        if (receipt is null)
            return NotFound(new { code = "RECEIPT_NOT_FOUND", message = "Recibo no encontrado." });
        if (!BelongsToTenant(caller!, receipt.OwnerAdminId))
            return ForbiddenTenant(caller!, "receipt", receiptId);
        if (!caller!.IsAdmin && !string.Equals(receipt.ResidentExternalId, caller.UserExternalId, StringComparison.Ordinal))
            return ForbiddenTenant(caller, "receipt", receiptId);
        if (!FeeStatus.IsPaid(receipt.Status))
            return Conflict(new { code = "RECEIPT_NOT_PAID", message = "El recibo aún no está pagado." });

        var doc = await receiptDocumentService.ForReceiptAsync(receipt, ct);
        return File(doc.Content, "application/pdf", doc.FileName);
    }

    /// <summary>RF030: ingresos, egresos y balance neto del mes "yyyy-MM" (por defecto el actual).</summary>
    [HttpGet("balance")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetMonthlyBalance([FromQuery] string? month, CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (error is not null) return error;

        string monthKey;
        if (string.IsNullOrWhiteSpace(month))
            monthKey = Period.FromDate(FinanceClock.Today()).Key;
        else if (Period.Normalize(month) is { } normalized)
            monthKey = normalized;
        else
            return BadRequest(new { code = "INVALID_MONTH", field = "month", message = "El mes debe tener formato yyyy-MM." });

        var balance = await dashboardQueryService.GetMonthlyBalanceAsync(caller!.TenantId, monthKey, ct);
        return Ok(new
        {
            month = balance.MonthKey,
            income = balance.Income,
            expenses = balance.Expenses,
            net = balance.Net,
        });
    }

    private IActionResult BillingResult(MonthlyBillingResult result)
    {
        var payload = new
        {
            month = result.Month,
            created = result.Created,
            alreadyBilled = result.AlreadyBilled,
            notDue = result.NotDue,
            skipped = result.Skipped,
            reason = result.Reason,
        };

        return result.Reason switch
        {
            MonthlyBillingService.ReasonInvalidMonth => BadRequest(new
            {
                code = result.Reason,
                field = "month",
                message = "El mes debe tener formato yyyy-MM.",
            }),
            MonthlyBillingService.ReasonMissingCharge => UnprocessableEntity(new
            {
                code = result.Reason,
                message = $"Configura la cuota de mantenimiento de {result.Month} antes de facturar.",
                result = payload,
            }),
            _ => Ok(payload),
        };
    }

    private static string? BodyOrNull(JsonElement? body, string name) =>
        body is { } b ? BodyString(b, name) : null;
}
