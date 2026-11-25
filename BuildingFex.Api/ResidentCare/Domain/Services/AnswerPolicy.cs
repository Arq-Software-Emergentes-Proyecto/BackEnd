using System.Text;
using System.Text.RegularExpressions;
using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

namespace BuildingFex.Api.ResidentCare.Domain.Services;

/// <summary>
/// Domain service de Resident Care (informe 5.9.1): el asistente solo responde con una fuente vigente
/// del mismo edificio; si no hay fuente, deriva a la junta / administración. Tampoco decide sobre
/// emergencias: las deriva sin pasar por la IA.
/// </summary>
public static class AnswerPolicy
{
    /// <summary>
    /// Consultas con IA que cada residente puede hacer por día (CON11), además del tope mensual del edificio:
    /// una sola persona no puede agotar las consultas de todos.
    /// </summary>
    public const int ResidentDailyAiQueries = 15;

    private static readonly Regex CitationLine = new(
        @"^[ \t>*_\-]*(?:fuentes?|sources?)[ \t*_]*:[ \t*_]*(?<src>.+?)[ \t*_.]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly string[] EmergencyPhrases =
    [
        "incendio", "fuga de gas", "olor a gas", "huele a gas", "emergencia", "inundacion", "electrocut",
        "fire in", "gas leak", "smell gas", "smells like gas", "emergency", "flooding",
    ];

    /// <summary>Sin fuente documental relevante → derivar sin inventar (US033 "Consulta sin base documental").</summary>
    public static bool MustDerive(IReadOnlyCollection<KnowledgeSource> sources) => sources.Count == 0;

    /// <summary>Emergencias (incendio, gas, inundación…): no las resuelve la IA; se orienta y se deriva.</summary>
    public static bool IsEmergency(string text)
    {
        var q = SupportEscalate.Fold(text);
        return !string.IsNullOrEmpty(q) && EmergencyPhrases.Any(p => q.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>Bloque SOURCES del prompt: cada fragmento con su nombre de archivo para que se cite.</summary>
    public static string BuildContext(IReadOnlyList<KnowledgeSource> sources)
    {
        if (sources.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            sb.AppendLine($"--- source: {s.FileName} ---");
            sb.AppendLine(s.Content.Trim());
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Interpreta la salida del modelo: marcador de derivación, línea "Fuente: …" (se quita del texto y se
    /// guarda aparte) y la asocia a uno de los fragmentos recuperados. Una fuente que no se recuperó no se acepta.
    /// </summary>
    public static Answer ToAnswer(string rawReply, IReadOnlyList<KnowledgeSource> sources)
    {
        var derive = SupportEscalate.AssistantWantsEscalate(rawReply);
        var text = SupportEscalate.StripEscalateMarker(rawReply);

        string? cited = null;
        var matches = CitationLine.Matches(text);
        if (matches.Count > 0)
        {
            cited = matches[^1].Groups["src"].Value.Trim().Trim('"', '\'', '«', '»', '`');
            text = CitationLine.Replace(text, string.Empty);
            text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        }

        var source = MatchSource(cited, sources);
        if (source is null && !derive && sources.Count > 0)
            source = sources[0];

        return new Answer(
            text,
            source?.DocumentId,
            source?.FileName,
            Grounded: source is not null && !derive,
            MustDerive: derive);
    }

    /// <summary>Texto del asistente con su cita, para reenviar el historial al modelo.</summary>
    public static string WithCitation(string body, string? citedSource, string locale)
    {
        if (string.IsNullOrWhiteSpace(citedSource)) return body;
        var label = locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Source" : "Fuente";
        return $"{body}\n{label}: {citedSource}";
    }

    public static string NoDocumentaryBasisMessage(string locale) => IsEnglish(locale)
        ? "I couldn't find anything about this in your building's current rules or meeting minutes, so I can't answer it without guessing. I've passed your question to the board / building administration; they will reply in this same chat."
        : "No encontré una base documental en el reglamento ni en las actas vigentes de tu edificio para responder esto, así que no puedo contestarte sin adivinar. Derivé tu consulta a la junta / administración; te responderán en este mismo chat.";

    public static string MonthlyCapReachedMessage(string locale) => IsEnglish(locale)
        ? "Your building has reached this month's assistant query limit for its plan, so I can't use AI to answer. I've passed your question to the board / building administration; they will reply in this same chat."
        : "Tu edificio alcanzó el tope mensual de consultas al asistente de su plan, así que no puedo usar la IA para responder. Derivé tu consulta a la junta / administración; te responderán en este mismo chat.";

    public static string ResidentDailyLimitMessage(string locale, int limit) => IsEnglish(locale)
        ? $"You've reached today's limit of {limit} assistant questions. You can ask again tomorrow; if it's urgent, ask to talk to the building administration."
        : $"Llegaste al límite de {limit} consultas al asistente por hoy. Podrás volver a preguntar mañana; si es urgente, pide hablar con la administración.";

    public static string EmergencyMessage(string locale) => IsEnglish(locale)
        ? "If this is an emergency, call emergency services now (Peru: firefighters 116, police 105, SAMU 106) and alert the building staff. I've also passed your message to the building administration."
        : "Si es una emergencia, llama ahora a los servicios de emergencia (Bomberos 116, Policía 105, SAMU 106) y avisa al personal del edificio. También derivé tu mensaje a la administración.";

    private static KnowledgeSource? MatchSource(string? cited, IReadOnlyList<KnowledgeSource> sources)
    {
        if (string.IsNullOrWhiteSpace(cited) || sources.Count == 0) return null;
        var c = SupportEscalate.Fold(cited);
        return sources.FirstOrDefault(s =>
        {
            var f = SupportEscalate.Fold(s.FileName);
            return f.Length > 0 && (c.Contains(f, StringComparison.Ordinal) || f.Contains(c, StringComparison.Ordinal));
        });
    }

    private static bool IsEnglish(string locale) =>
        (locale ?? string.Empty).StartsWith("en", StringComparison.OrdinalIgnoreCase);
}
