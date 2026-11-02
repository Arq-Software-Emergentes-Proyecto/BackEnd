using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildingFex.Api.Finances.Domain.Model.ValueObjects;

/// <summary>
/// Cuota mensual configurada por el admin (finance_settings.monthly_charges_json).
/// Mismo contrato que billingCycle.normalizeCharge del frontend: el periodo es el mes facturado
/// y el pago abre el 1.º del mes siguiente y vence 6 días después (por defecto el 7).
/// </summary>
public sealed record MonthlyCharge(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("billingStart")] string BillingStart,
    [property: JsonPropertyName("billingEnd")] string BillingEnd,
    [property: JsonPropertyName("paymentOpen")] string PaymentOpen,
    [property: JsonPropertyName("paymentDue")] string PaymentDue)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public bool IsBillable => Amount > 0 && !string.IsNullOrWhiteSpace(PaymentDue);

    public static MonthlyCharge? Normalize(string? month, decimal amount, string? paymentOpen, string? paymentDue, string? id = null)
    {
        if (!Period.TryParse(month, out var period)) return null;
        var next = period.Next();
        var defaultOpen = next.FirstDay;
        var open = TryParseYmd(paymentOpen, out var o) ? o : defaultOpen;
        var due = TryParseYmd(paymentDue, out var d) ? d : open.AddDays(6);

        return new MonthlyCharge(
            string.IsNullOrWhiteSpace(id) ? $"charge-{period.Key}" : id.Trim(),
            period.Key,
            decimal.Round(amount, 2),
            Ymd(period.FirstDay),
            Ymd(period.LastDay),
            Ymd(open),
            Ymd(due));
    }

    /// <summary>Parsea el JSON guardado (o el array del body) a cargos únicos por mes, del más reciente al más antiguo.</summary>
    public static IReadOnlyList<MonthlyCharge> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseList(doc.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<MonthlyCharge> ParseList(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) return [];
        var result = new Dictionary<string, MonthlyCharge>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var charge = Normalize(
                Str(item, "month"),
                Dec(item, "amount"),
                Str(item, "paymentOpen"),
                Str(item, "paymentDue"),
                Str(item, "id"));
            if (charge is null || result.ContainsKey(charge.Month)) continue;
            result[charge.Month] = charge;
        }
        return result.Values.OrderByDescending(c => c.Month, StringComparer.Ordinal).ToList();
    }

    public static string ToJson(IEnumerable<MonthlyCharge> charges) =>
        JsonSerializer.Serialize(charges.ToList(), JsonOptions);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static decimal Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String
            && decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    public static bool TryParseYmd(string? value, out DateOnly date)
    {
        date = default;
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length < 10) return false;
        return DateOnly.TryParseExact(raw[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);
    }

    public static string Ymd(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
