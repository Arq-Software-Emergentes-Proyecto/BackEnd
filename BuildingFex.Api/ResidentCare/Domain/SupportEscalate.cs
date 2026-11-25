using System.Globalization;
using System.Text;

namespace BuildingFex.Api.ResidentCare.Domain;

/// <summary>Normalización y utilidades de texto para soporte (US042 / US044 / US045).</summary>
public static class SupportEscalate
{
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
