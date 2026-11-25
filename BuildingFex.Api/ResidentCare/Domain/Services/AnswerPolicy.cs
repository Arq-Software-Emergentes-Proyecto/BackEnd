using System.Text;
using System.Text.RegularExpressions;
using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

namespace BuildingFex.Api.ResidentCare.Domain.Services;

/// <summary>
/// Servicio de dominio: el asistente solo responde con fuentes vigentes del mismo edificio o la guía de uso y cita la fuente usada.
/// Sin fuente, o si la consulta necesita a una persona, deriva a la junta / administración; las emergencias no las resuelve la IA (US044).
/// </summary>
public static class AnswerPolicy
{
    private static readonly string[] EmergencyPhrases =
    [
        "incendio", "fuga de gas", "olor a gas", "huele a gas", "emergencia", "inundacion", "electrocut",
        "fire in", "gas leak", "smell gas", "smells like gas", "emergency", "flooding",
    ];

    /// <summary>Sin fuente documental relevante → derivar sin inventar.</summary>
    public static bool MustDerive(IReadOnlyCollection<KnowledgeSource> sources) => sources.Count == 0;

    /// <summary>Emergencias (incendio, gas, inundación…): se orienta y se deriva sin pasar por la IA.</summary>
    public static bool IsEmergency(string? text)
    {
        var q = SupportEscalate.Fold(text);
        return q.Length > 0 && EmergencyPhrases.Any(p => q.Contains(p, StringComparison.Ordinal));
    }

    private static readonly Regex CitationLineRegex = new(
        @"^[\s>*_\-]*(?:fuentes?|sources?)\s*:\s*(.+?)[*_\.\s]*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex ExtraNewlinesRegex = new(
        @"\n{3,}",
        RegexOptions.Compiled);

    public static string BuildContext(IReadOnlyList<KnowledgeSource> sources)
    {
        if (sources.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var source in sources)
        {
            sb.Append("--- source: ").Append(source.FileName).AppendLine(" ---");
            sb.AppendLine(source.Content);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static Answer ToAnswer(string rawReply, IReadOnlyList<KnowledgeSource> sources)
    {
        var derive = SupportEscalate.AssistantWantsEscalate(rawReply);
        var raw = SupportEscalate.StripEscalateMarker(rawReply);
        var matches = CitationLineRegex.Matches(raw);
        var citedRaw = matches.Count > 0 ? CleanCitationName(matches[^1].Groups[1].Value) : null;

        var withoutCitations = CitationLineRegex.Replace(raw, string.Empty).Replace("\r\n", "\n");
        var cleanText = ExtraNewlinesRegex.Replace(withoutCitations, "\n\n").Trim();

        // Si el modelo deriva, la respuesta no se presenta como respaldada por un documento.
        if (sources.Count == 0 || derive)
            return new Answer(cleanText, null, null, Grounded: false, MustDerive: derive);

        var matched = MatchSource(citedRaw, sources) ?? sources[0];
        return new Answer(cleanText, matched.DocumentId, matched.FileName, Grounded: true);
    }

    public static string WithCitation(string body, string? citedSource, string locale)
    {
        if (string.IsNullOrWhiteSpace(citedSource)) return body;
        var label = locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Source" : "Fuente";
        return $"{body}\n{label}: {citedSource.Trim()}";
    }

    public static string NoDocumentaryBasisMessage(string locale) =>
        locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "I couldn't find a documentary basis in your building's current bylaws or meeting minutes to answer this, so I can't reply without guessing. I've passed your question to the board / building administration; they will reply in this same chat."
            : "No encontré una base documental en el reglamento ni en las actas vigentes de tu edificio para responder esto, así que no puedo contestarte sin adivinar. Derivé tu consulta a la junta / administración; te responderán en este mismo chat.";

    public static string MonthlyCapReachedMessage(string locale) =>
        locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "Your building has reached this month's assistant query limit for its plan, so I can't use AI to answer. I've passed your question to the board / building administration; they will reply in this same chat."
            : "Tu edificio alcanzó el tope mensual de consultas al asistente de su plan, así que no puedo usar la IA para responder. Derivé tu consulta a la junta / administración; te responderán en este mismo chat.";

    public static string EmergencyMessage(string locale) =>
        locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "If this is an emergency, call emergency services now (Peru: firefighters 116, police 105, SAMU 106) and alert the building staff. I've also passed your message to the building administration."
            : "Si es una emergencia, llama ahora a los servicios de emergencia (Bomberos 116, Policía 105, SAMU 106) y avisa al personal del edificio. También derivé tu mensaje a la administración.";

    private static string? CleanCitationName(string raw)
    {
        var clean = raw.Trim().Trim('"', '\'', '«', '»', '`', '*', '_', '.', ' ');
        return clean.Length == 0 ? null : clean;
    }

    private static KnowledgeSource? MatchSource(string? citedName, IReadOnlyList<KnowledgeSource> sources)
    {
        if (string.IsNullOrWhiteSpace(citedName)) return null;
        var foldedCited = SupportEscalate.Fold(citedName);
        if (foldedCited.Length == 0) return null;

        foreach (var source in sources)
        {
            var foldedSource = SupportEscalate.Fold(source.FileName);
            if (foldedSource.Length == 0) continue;
            if (foldedSource.Contains(foldedCited, StringComparison.Ordinal)
                || foldedCited.Contains(foldedSource, StringComparison.Ordinal))
            {
                return source;
            }
        }
        return null;
    }
}
