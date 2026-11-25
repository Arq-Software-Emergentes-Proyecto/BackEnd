using System.Text;
using System.Text.RegularExpressions;

namespace BuildingFex.Api.ResidentCare.Domain;

public static class SupportEscalate
{
    public const string EscalateToken = "[[ESCALATE_ADMIN]]";

    private static readonly string[] Phrases =
    [
        "pasame con el administrador",
        "pasame con administracion",
        "pasame al administrador",
        "pasame a administracion",
        "conectame con el administrador",
        "hablar con administracion",
        "hablar con el administrador",
        "quiero hablar con el administrador",
        "quiero un administrador",
        "transferir a administracion",
        "pass me to the admin",
        "pass me to admin",
        "talk to admin",
        "talk to building admin",
        "speak to an administrator",
        "speak with admin",
        "connect me with admin",
        "transfer me to admin",
    ];

    private static readonly Regex[] Patterns =
    [
        new(@"pasa(me|nos).{0,24}admin", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"conecta(me|nos).{0,24}admin", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"hablar.{0,24}admin", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"pass me.{0,24}admin", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"talk to.{0,20}admin", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"speak (to|with).{0,20}admin", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"transfer me.{0,24}admin", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static bool ResidentWantsAdmin(string text)
    {
        var q = Fold(text);
        if (string.IsNullOrEmpty(q)) return false;
        if (Phrases.Any(p => q.Contains(p, StringComparison.Ordinal))) return true;
        return Patterns.Any(p => p.IsMatch(q));
    }

    public static bool AssistantWantsEscalate(string text)
        => (text ?? string.Empty).Contains(EscalateToken, StringComparison.Ordinal);

    public static string StripEscalateMarker(string text)
    {
        var cleaned = Regex.Replace(text ?? string.Empty, @"\s*\[\[ESCALATE_ADMIN\]\]\s*", "\n");
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
    }

    internal static string Fold(string text)
    {
        var normalized = (text ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch is '¿' or '?' or '¡' or '!' or '.' or ',' or ';' or ':' or '\'' or '"')
            {
                sb.Append(' ');
                continue;
            }
            sb.Append(ch);
        }

        return Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }
}
