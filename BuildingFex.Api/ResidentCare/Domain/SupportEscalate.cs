using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BuildingFex.Api.ResidentCare.Domain;

/// <summary>Normalización y utilidades de texto para soporte (US042 / US044 / US045).</summary>
public static class SupportEscalate
{
    /// <summary>Marcador que el modelo pone en la última línea cuando la consulta necesita a una persona (US044).</summary>
    public const string EscalateToken = "[[ESCALATE_ADMIN]]";

    private static readonly string[] AdminPhrases =
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
        "hablar con la junta",
        "pass me to the admin",
        "pass me to admin",
        "talk to admin",
        "talk to building admin",
        "speak to an administrator",
        "speak with admin",
        "connect me with admin",
        "transfer me to admin",
    ];

    private static readonly Regex[] AdminPatterns =
    [
        new(@"pasa(me|nos).{0,24}admin", RegexOptions.Compiled),
        new(@"conecta(me|nos).{0,24}admin", RegexOptions.Compiled),
        new(@"hablar.{0,24}admin", RegexOptions.Compiled),
        new(@"pass me.{0,24}admin", RegexOptions.Compiled),
        new(@"talk to.{0,20}admin", RegexOptions.Compiled),
        new(@"speak (to|with).{0,20}admin", RegexOptions.Compiled),
        new(@"transfer me.{0,24}admin", RegexOptions.Compiled),
    ];

    private static readonly Regex EscalateMarker = new(@"\s*\[\[ESCALATE_ADMIN\]\]\s*", RegexOptions.Compiled);
    private static readonly Regex ExtraNewlines = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>El residente pide hablar con la administración o la junta (se deriva sin llamar a la IA).</summary>
    public static bool ResidentWantsAdmin(string? text)
    {
        var q = Fold(text);
        if (q.Length == 0) return false;
        return AdminPhrases.Any(p => q.Contains(p, StringComparison.Ordinal)) || AdminPatterns.Any(p => p.IsMatch(q));
    }

    public static bool AssistantWantsEscalate(string? text) =>
        (text ?? string.Empty).Contains(EscalateToken, StringComparison.Ordinal);

    /// <summary>Quita el marcador del texto que ve el residente.</summary>
    public static string StripEscalateMarker(string? text)
    {
        var cleaned = EscalateMarker.Replace(text ?? string.Empty, "\n");
        return ExtraNewlines.Replace(cleaned, "\n\n").Trim();
    }

    /// <summary>Minúsculas, sin tildes (FormD), signos de puntuación a espacio y espacios colapsados.</summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            var mapped = ch is '¿' or '?' or '¡' or '!' or '.' or ',' or ';' or ':' or '\'' or '"'
                ? ' '
                : ch;

            if (char.IsWhiteSpace(mapped))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(mapped);
                lastWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }
}
