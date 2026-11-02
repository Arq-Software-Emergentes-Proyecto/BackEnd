namespace BuildingFex.Api.Finances.Domain.Model.ValueObjects;

/// <summary>
/// Estados de una cuota / recibo (Tabla 60: PENDING, PAID, OVERDUE).
/// Se persisten como texto por compatibilidad con el frontend ("Pending" / "Paid" / "Overdue")
/// y con los registros legacy en español ("Pendiente" / "Pagado" / "En Mora").
/// </summary>
public static class FeeStatus
{
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    public const string Overdue = "Overdue";

    // Valores legacy (tabla fees / seed db.json).
    public const string PendingEs = "Pendiente";
    public const string PaidEs = "Pagado";
    public const string OverdueEs = "En Mora";

    public static bool IsPaid(string? status) =>
        string.Equals(status, Paid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, PaidEs, StringComparison.OrdinalIgnoreCase);

    public static bool IsOverdue(string? status) =>
        string.Equals(status, Overdue, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, OverdueEs, StringComparison.OrdinalIgnoreCase);

    /// <summary>Normaliza cualquier variante al valor canónico en inglés.</summary>
    public static string Normalize(string? status)
    {
        if (IsPaid(status)) return Paid;
        if (IsOverdue(status)) return Overdue;
        return Pending;
    }
}
