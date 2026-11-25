using System.Text.RegularExpressions;

namespace BuildingFex.Api.ResidentCare.Domain.Services;

public enum QuestionScope { Building, SmallTalk, OffTopic, Unclear }

/// <summary>
/// Servicio de dominio (US045): clasifica la pregunta del residente sin IA para no gastar consultas
/// en saludos ni en temas ajenos al edificio. Ante la duda, deja pasar (Building).
/// </summary>
public static class QuestionRelevancePolicy
{
    private static readonly Regex ArithmeticRegex = new(
        @"^(?:cuanto (?:es|son|da|seria)|how much is|what is|calcula|calculate)?\s*\d+(?:[.,]\d+)?(?:\s*[+\-*/x×÷]\s*\d+(?:[.,]\d+)?)+\s*$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> GreetingWords = new(StringComparer.Ordinal)
    {
        "hola", "holas", "buenas", "buenos", "buen", "dia", "dias", "tardes", "noches", "hey", "hi", "hello",
        "good", "morning", "afternoon", "evening", "saludos", "que", "tal", "como", "estas", "esta", "todo", "bien",
        "gracias", "muchas", "muchisimas", "mil", "mucho", "thanks", "thank", "you", "thx", "ty", "ok", "okay",
        "listo", "vale", "perfecto", "genial", "excelente", "entendido", "claro", "chau", "adios", "bye",
        "hasta", "luego", "un", "favor", "por", "very", "much", "so",
    };

    private static readonly string[] ThanksWords = ["gracias", "thanks", "thank", "thx", "ty"];

    // Pedidos claramente ajenos, aunque se mencione algo del edificio.
    private static readonly string[] OffTopicRequests =
    [
        "chiste", "chistes", "joke", "jokes", "haz mi tarea", "hazme la tarea", "mi tarea", "homework",
        "escribe codigo", "escribeme codigo", "write code", "write a program", "python", "javascript",
        "traduce", "traducir", "translate", "poema", "poem", "cancion", "song lyrics", "receta de", "recipe for",
        "horoscopo", "horoscope", "ensayo sobre", "essay about",
    ];

    // Temas generales: solo son ajenos si no aparece ninguna palabra del edificio.
    private static readonly string[] GeneralTopics =
    [
        "noticias", "news", "futbol", "football", "soccer", "clima", "weather", "politica", "politics",
        "elecciones", "elections", "farandula", "celebridad", "celebrity", "bitcoin", "criptomonedas",
        "pelicula", "movie", "videojuego", "videogame", "presidente", "capital de", "capital of",
    ];

    private static readonly string[] BuildingWords =
    [
        "edificio", "building", "condominio", "condominium", "departamento", "depa", "apartment", "residente",
        "resident", "reglamento", "bylaws", "acta", "actas", "minutes", "junta", "board", "administracion",
        "administrador", "admin", "administration", "cuota", "cuotas", "fee", "fees", "mantenimiento",
        "maintenance", "pago", "pagos", "payment", "recibo", "receipt", "incidencia", "incidencias", "incident",
        "reserva", "reservar", "reservation", "piscina", "pool", "gimnasio", "gym", "ascensor", "elevator",
        "estacionamiento", "cochera", "parking", "vecino", "vecinos", "neighbor", "ruido", "noise", "fuga",
        "leak", "aviso", "avisos", "buildingfex", "porteria", "lobby", "terraza", "salon", "basura", "garbage",
        "areas comunes", "common area", "visita", "visitas", "paquete", "paquetes", "multa", "fine",
    ];

    public static QuestionScope Classify(string? text)
    {
        var folded = SupportEscalate.Fold(text);
        if (folded.Count(char.IsLetter) < 2) return QuestionScope.Unclear;

        if (ArithmeticRegex.IsMatch(folded)) return QuestionScope.OffTopic;

        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 5 && words.All(GreetingWords.Contains)) return QuestionScope.SmallTalk;

        var padded = $" {folded} ";
        if (ContainsAny(padded, OffTopicRequests)) return QuestionScope.OffTopic;
        if (ContainsAny(padded, GeneralTopics) && !ContainsAny(padded, BuildingWords)) return QuestionScope.OffTopic;

        return QuestionScope.Building;
    }

    private static bool ContainsAny(string paddedText, string[] terms) =>
        terms.Any(t => paddedText.Contains($" {t} ", StringComparison.Ordinal));

    private static bool IsEnglish(string locale) => locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>Respuesta fija a un saludo o agradecimiento.</summary>
    public static string SmallTalkMessage(string locale, string? text = null)
    {
        var folded = SupportEscalate.Fold(text);
        var thanks = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => ThanksWords.Contains(w));
        if (IsEnglish(locale))
            return thanks
                ? "You're welcome! Ask me anything about your building's bylaws, meeting minutes or incidents whenever you need."
                : "Hi! I'm the BuildingFex assistant. Ask me about your building's bylaws, meeting minutes or how to report an incident.";
        return thanks
            ? "¡De nada! Pregúntame lo que necesites sobre el reglamento, las actas o las incidencias de tu edificio."
            : "¡Hola! Soy el asistente de BuildingFex. Pregúntame sobre el reglamento, las actas o cómo reportar una incidencia en tu edificio.";
    }

    public static string OffTopicMessage(string locale) =>
        IsEnglish(locale)
            ? "I can only help with your building and BuildingFex, so I can't answer that. This didn't use any of your assistant queries."
            : "Solo puedo ayudarte con temas de tu edificio y de BuildingFex, así que no puedo responder eso. Esta consulta no gastó ninguna de tus consultas al asistente.";

    public static string UnclearMessage(string locale) =>
        IsEnglish(locale)
            ? "I didn't understand your message. Could you tell me what you need about your building? This didn't use any of your queries."
            : "No entendí tu mensaje. ¿Me cuentas qué necesitas sobre tu edificio? Esta consulta no gastó ninguna de tus consultas.";
}
