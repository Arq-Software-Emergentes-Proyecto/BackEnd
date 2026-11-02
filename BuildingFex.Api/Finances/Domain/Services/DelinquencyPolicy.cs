using BuildingFex.Api.Finances.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Finances.Domain.Services;

/// <summary>Color del semáforo de deudas (RF029 / US029).</summary>
public enum DebtLight
{
    /// <summary>Sin cuota emitida para el mes.</summary>
    None,
    /// <summary>Pagada (verde).</summary>
    Paid,
    /// <summary>Pendiente con más de <see cref="DelinquencyPolicy.DueSoonDays"/> días de plazo (gris).</summary>
    Pending,
    /// <summary>Vence en ≤ <see cref="DelinquencyPolicy.DueSoonDays"/> días (ámbar).</summary>
    DueSoon,
    /// <summary>Vencida (rojo).</summary>
    Overdue,
}

/// <summary>
/// Servicio de dominio de morosidad (Tabla 60 – DelinquencyPolicy).
/// Una cuota está vencida cuando no está pagada y la fecha de hoy superó su vencimiento
/// (pagar el mismo día del vencimiento sigue siendo puntual, igual que el recargo diario del frontend).
/// Es morosa (isDelinquent) si lleva más de <see cref="DelinquencyDays"/> días vencida.
/// La morosidad del edificio es crítica cuando supera <see cref="DefaultCriticalThreshold"/> del presupuesto mensual (US023).
/// </summary>
public static class DelinquencyPolicy
{
    public const int DueSoonDays = 5;
    public const int DelinquencyDays = 30;
    public const decimal DefaultCriticalThreshold = 0.15m;

    public static bool IsOverdue(string? status, string? dueDate, DateOnly today)
    {
        if (FeeStatus.IsPaid(status)) return false;
        if (FeeStatus.IsOverdue(status)) return true;
        return MonthlyCharge.TryParseYmd(dueDate, out var due) && today > due;
    }

    public static int DaysOverdue(string? status, string? dueDate, DateOnly today)
    {
        if (FeeStatus.IsPaid(status) || !MonthlyCharge.TryParseYmd(dueDate, out var due)) return 0;
        var days = today.DayNumber - due.DayNumber;
        return days > 0 ? days : 0;
    }

    public static bool IsDelinquent(string? status, string? dueDate, DateOnly today) =>
        DaysOverdue(status, dueDate, today) > DelinquencyDays;

    public static DebtLight Classify(string? status, string? dueDate, DateOnly today)
    {
        if (FeeStatus.IsPaid(status)) return DebtLight.Paid;
        if (IsOverdue(status, dueDate, today)) return DebtLight.Overdue;
        if (MonthlyCharge.TryParseYmd(dueDate, out var due) && due.DayNumber - today.DayNumber <= DueSoonDays)
            return DebtLight.DueSoon;
        return DebtLight.Pending;
    }

    /// <summary>US023: deuda vencida mayor al umbral (15 %) del presupuesto mensual esperado.</summary>
    public static bool IsCritical(decimal overdueDebt, decimal monthlyBudget, decimal threshold = DefaultCriticalThreshold)
    {
        if (overdueDebt <= 0) return false;
        if (monthlyBudget <= 0) return false;
        return overdueDebt > monthlyBudget * threshold;
    }

    public static string ToApiValue(DebtLight light) => light switch
    {
        DebtLight.Paid => "paid",
        DebtLight.Pending => "pending",
        DebtLight.DueSoon => "dueSoon",
        DebtLight.Overdue => "overdue",
        _ => "none",
    };
}
