using System.Globalization;
using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
using BuildingFex.Api.Finances.Domain.Services;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Finances.Application.Internal.Delinquency;

/// <summary>Celda del semáforo: estado de un residente en un mes contable.</summary>
public sealed record PaymentCalendarCell(
    string Month,
    string Status,
    decimal Amount,
    string? DueDate,
    int DaysOverdue,
    IReadOnlyList<string> ReceiptIds);

public sealed record PaymentCalendarRow(
    string ResidentId,
    string Name,
    string? Floor,
    string? Code,
    decimal OverdueAmount,
    bool HasOverdue,
    IReadOnlyList<PaymentCalendarCell> Cells);

public sealed record PaymentCalendarResponse(
    string Today,
    int DueSoonDays,
    IReadOnlyList<string> Months,
    IReadOnlyList<PaymentCalendarRow> Residents);

public interface IPaymentCalendarQueryService
{
    Task<PaymentCalendarResponse> GetAsync(
        int ownerAdminId,
        string? residentExternalId,
        int months,
        CancellationToken ct = default);
}

/// <summary>
/// Panel de deudas semaforizado (RF029 / US029): residente × últimos N meses.
/// Verde = pagado, ámbar = vence en ≤ 5 días, rojo = vencido (DelinquencyPolicy), gris = pendiente, vacío = sin cuota.
/// Fuente: receipts por billing_month (legacy: mes anterior al vencimiento) y fees legacy por su mes.
/// </summary>
public class PaymentCalendarQueryService(AppDbContext db) : IPaymentCalendarQueryService
{
    private static readonly string[] SpanishMonths =
    [
        "enero", "febrero", "marzo", "abril", "mayo", "junio",
        "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
    ];

    private sealed record Item(string ResidentId, string Month, string ExternalId, string Status, string? DueDate, decimal Amount);

    public async Task<PaymentCalendarResponse> GetAsync(
        int ownerAdminId,
        string? residentExternalId,
        int months,
        CancellationToken ct = default)
    {
        months = Math.Clamp(months, 1, 24);
        var today = FinanceClock.Today();
        var current = Period.FromDate(today);
        var monthKeys = Enumerable.Range(0, months)
            .Select(i => current.AddMonths(i - (months - 1)).Key)
            .ToList();
        var monthSet = monthKeys.ToHashSet(StringComparer.Ordinal);

        var residentsQuery = db.Set<User>()
            .AsNoTracking()
            .Where(u => u.Role == "resident" && u.OwnerAdminId == ownerAdminId);
        if (!string.IsNullOrWhiteSpace(residentExternalId))
            residentsQuery = residentsQuery.Where(u => u.ExternalId == residentExternalId);
        var residents = await residentsQuery.ToListAsync(ct);

        var receiptsQuery = db.Set<Receipt>().AsNoTracking().Where(r => r.OwnerAdminId == ownerAdminId);
        var feesQuery = db.Set<Fee>().AsNoTracking().Where(f => f.OwnerAdminId == ownerAdminId);
        if (!string.IsNullOrWhiteSpace(residentExternalId))
        {
            receiptsQuery = receiptsQuery.Where(r => r.ResidentExternalId == residentExternalId);
            feesQuery = feesQuery.Where(f => f.ResidentExternalId == residentExternalId);
        }

        var items = new List<Item>();
        foreach (var r in await receiptsQuery.ToListAsync(ct))
        {
            var month = r.EffectiveBillingMonth();
            if (month is not null && monthSet.Contains(month))
                items.Add(new Item(r.ResidentExternalId, month, r.ExternalId, r.Status, r.DueDate, r.Total));
        }
        foreach (var f in await feesQuery.ToListAsync(ct))
        {
            var month = FeeMonthKey(f);
            if (month is not null && monthSet.Contains(month))
                items.Add(new Item(f.ResidentExternalId, month, f.ExternalId, f.Status, f.DueDate, f.Amount));
        }

        var byResident = items.GroupBy(i => i.ResidentId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var rows = residents
            .OrderBy(r => r.Floor ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Code ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(resident =>
            {
                var own = byResident.TryGetValue(resident.ExternalId, out var list) ? list : [];
                var cells = monthKeys.Select(m => BuildCell(m, own.Where(i => i.Month == m).ToList(), today)).ToList();
                var overdueAmount = cells.Where(c => c.Status == DelinquencyPolicy.ToApiValue(DebtLight.Overdue)).Sum(c => c.Amount);
                return new PaymentCalendarRow(
                    resident.ExternalId,
                    resident.Name,
                    resident.Floor,
                    resident.Code,
                    overdueAmount,
                    overdueAmount > 0,
                    cells);
            })
            .ToList();

        return new PaymentCalendarResponse(
            MonthlyCharge.Ymd(today),
            DelinquencyPolicy.DueSoonDays,
            monthKeys,
            rows);
    }

    private static PaymentCalendarCell BuildCell(string month, List<Item> items, DateOnly today)
    {
        if (items.Count == 0)
            return new PaymentCalendarCell(month, DelinquencyPolicy.ToApiValue(DebtLight.None), 0, null, 0, []);

        // El peor estado manda: vencido > por vencer > pendiente > pagado.
        var worst = items
            .Select(i => DelinquencyPolicy.Classify(i.Status, i.DueDate, today))
            .OrderByDescending(Severity)
            .First();
        var unpaid = items.Where(i => !FeeStatus.IsPaid(i.Status)).ToList();
        var relevant = unpaid.Count > 0 ? unpaid : items;
        var dueDate = relevant
            .Select(i => i.DueDate)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .OrderBy(d => d, StringComparer.Ordinal)
            .FirstOrDefault();

        return new PaymentCalendarCell(
            month,
            DelinquencyPolicy.ToApiValue(worst),
            relevant.Sum(i => i.Amount),
            dueDate,
            unpaid.Select(i => DelinquencyPolicy.DaysOverdue(i.Status, i.DueDate, today)).DefaultIfEmpty(0).Max(),
            items.Select(i => i.ExternalId).ToList());
    }

    private static int Severity(DebtLight light) => light switch
    {
        DebtLight.Overdue => 4,
        DebtLight.DueSoon => 3,
        DebtLight.Pending => 2,
        DebtLight.Paid => 1,
        _ => 0,
    };

    /// <summary>fees.month puede ser "2026-05" o "Mayo 2026"; si no, el mes del vencimiento.</summary>
    private static string? FeeMonthKey(Fee fee)
    {
        var normalized = Period.Normalize(fee.Month);
        if (normalized is not null) return normalized;

        var text = (fee.Month ?? string.Empty).Trim().ToLower(CultureInfo.GetCultureInfo("es-PE"));
        var idx = Array.FindIndex(SpanishMonths, m => text.Contains(m, StringComparison.Ordinal));
        var yearDigits = new string(text.Where(char.IsDigit).ToArray());
        if (idx >= 0 && yearDigits.Length == 4 && int.TryParse(yearDigits, out var year))
            return new Period(year, idx + 1).Key;

        return MonthlyCharge.TryParseYmd(fee.DueDate, out var due) ? Period.FromDate(due).Key : null;
    }
}
