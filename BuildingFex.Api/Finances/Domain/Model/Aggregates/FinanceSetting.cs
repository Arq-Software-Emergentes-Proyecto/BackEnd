using BuildingFex.Api.Finances.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Finances.Domain.Model.Aggregates;

public class FinanceSetting : IAuditableEntity, IOwnerScopedFinanceEntity
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public User? OwnerAdmin { get; private set; }
    public decimal BaseMonthlyExpense { get; private set; }
    public decimal LateFeeRate { get; private set; }

    /// <summary>Recargo fijo por día de atraso (S/). Lo aplica el frontend sobre late_fee.</summary>
    public decimal LateFeePerDay { get; private set; }

    /// <summary>Rango de días del mes de ingreso en que el residente nuevo ya paga ese mes.</summary>
    public int AdmissionPayFromDay { get; private set; } = 1;
    public int AdmissionPayToDay { get; private set; } = 7;

    /// <summary>JSON con las cuotas mensuales configuradas (<see cref="MonthlyCharge"/>). Base de la facturación automática.</summary>
    public string MonthlyChargesJson { get; private set; } = "[]";

    /// <summary>
    /// Reglamento del edificio: los residentes morosos (cuota vencida hace más de 30 días, DelinquencyPolicy)
    /// no pueden reservar áreas comunes hasta regularizar el pago.
    /// </summary>
    public bool RestrictReservationsWhenDelinquent { get; private set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public string? ResidentExternalId => null;

    private FinanceSetting() { }

    public static FinanceSetting Create(
        string externalId,
        int ownerAdminId,
        decimal baseMonthlyExpense,
        decimal lateFeeRate)
    {
        return new FinanceSetting
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId,
            BaseMonthlyExpense = baseMonthlyExpense,
            LateFeeRate = lateFeeRate,
        };
    }

    public void Patch(decimal? baseMonthlyExpense, decimal? lateFeeRate)
    {
        if (baseMonthlyExpense.HasValue)
            BaseMonthlyExpense = baseMonthlyExpense.Value;
        if (lateFeeRate.HasValue)
            LateFeeRate = lateFeeRate.Value;
    }

    public void PatchBillingRules(
        decimal? lateFeePerDay,
        int? admissionPayFromDay,
        int? admissionPayToDay,
        IReadOnlyList<MonthlyCharge>? monthlyCharges)
    {
        if (lateFeePerDay.HasValue)
            LateFeePerDay = Math.Max(0, lateFeePerDay.Value);

        var from = Math.Clamp(admissionPayFromDay ?? AdmissionPayFromDay, 1, 31);
        var to = Math.Clamp(admissionPayToDay ?? AdmissionPayToDay, 1, 31);
        AdmissionPayFromDay = Math.Min(from, to);
        AdmissionPayToDay = Math.Max(from, to);

        if (monthlyCharges is not null)
        {
            MonthlyChargesJson = MonthlyCharge.ToJson(monthlyCharges);
            var latest = monthlyCharges.OrderByDescending(c => c.Month, StringComparer.Ordinal).FirstOrDefault();
            if (latest is not null && latest.Amount > 0)
                BaseMonthlyExpense = latest.Amount;
        }
    }

    public void SetReservationRestriction(bool? restrict)
    {
        if (restrict.HasValue) RestrictReservationsWhenDelinquent = restrict.Value;
    }

    public IReadOnlyList<MonthlyCharge> GetMonthlyCharges() => MonthlyCharge.ParseList(MonthlyChargesJson);

    public MonthlyCharge? FindCharge(Period month) =>
        GetMonthlyCharges().FirstOrDefault(c => c.Month == month.Key);
}
