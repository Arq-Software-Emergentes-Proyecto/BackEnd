namespace BuildingFex.Api.Finances.Domain.Model.ValueObjects;

/// <summary>Monto y moneda (Tabla 60). BuildingFex opera solo en soles (PEN).</summary>
public readonly record struct Money(decimal Amount, string Currency = Money.Pen)
{
    public const string Pen = "PEN";

    public static Money Zero => new(0m);

    public static Money Of(decimal amount) => new(decimal.Round(amount, 2, MidpointRounding.AwayFromZero));

    public bool IsPositive() => Amount > 0m;

    public Money Add(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No se pueden sumar montos de monedas distintas.");
        return new Money(Amount + other.Amount, Currency);
    }

    public override string ToString() => $"S/ {Amount:N2}";
}
