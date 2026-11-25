using System.Text.RegularExpressions;

namespace BuildingFex.Api.ResidentCare.Domain.Services;

/// <summary>Resultado del filtro previo a la IA.</summary>
public enum QuestionScope
{
    /// <summary>Pregunta del edificio o de BuildingFex (o sin señal clara): sigue al RAG y a la IA.</summary>
    Building,

    /// <summary>Saludo, agradecimiento o despedida: respuesta fija, sin IA.</summary>
    SmallTalk,

    /// <summary>Tema ajeno al edificio (chistes, tareas, código, noticias…): respuesta fija, sin IA.</summary>
    OffTopic,

    /// <summary>Texto sin contenido útil (una letra, solo emojis o signos): se pide reformular, sin IA.</summary>
    Unclear,
}

/// <summary>
/// Filtro de relevancia del asistente (CON11): decide, sin llamar a la IA, si la consulta del residente es una
/// duda del edificio. Los saludos, los temas ajenos y los mensajes sin contenido se responden con un texto fijo
/// y no consumen el tope mensual de consultas del plan, así quienes entran solo a probar el chat no lo agotan.
/// Ante la duda la consulta se deja pasar: después el RAG solo llama a la IA si encuentra una fuente del edificio.
/// </summary>
public static class QuestionRelevancePolicy
{
    /// <summary>Palabras que indican que la consulta trata del edificio, la convivencia o la plataforma.</summary>
    private static readonly string[] BuildingTerms =
    [
        // Edificio y convivencia
        "edificio", "condominio", "departamento", "depa", "dpto", "piso", "torre", "bloque", "vecino", "vecinos",
        "reglamento", "regla", "reglas", "norma", "normas", "acta", "actas", "junta", "asamblea", "administracion",
        "administrador", "administradora", "conserje", "porteria", "portero", "vigilancia", "vigilante", "seguridad",
        "mascota", "mascotas", "perro", "perros", "gato", "gatos", "ruido", "ruidos", "fiesta", "musica", "horario",
        "horarios", "estacionamiento", "cochera", "cocheras", "basura", "reciclaje", "mudanza", "obra", "obras",
        "remodelacion", "multa", "multas", "sancion", "lavanderia", "azotea", "jardin", "ascensor", "ascensores",
        "escalera", "pasillo", "puerta", "llaves", "intercomunicador", "propietario", "inquilino", "alquiler",
        // Pagos y finanzas
        "pagar", "pago", "pagos", "cuota", "cuotas", "mantenimiento", "recibo", "recibos", "deuda", "saldo",
        "mora", "morosidad", "comprobante", "mercado pago", "tarjeta", "vence", "vencimiento",
        // Áreas comunes, invitados e incidencias
        "reservar", "reserva", "reservas", "area comun", "areas comunes", "piscina", "parrilla", "gimnasio", "salon",
        "sum", "cancha", "aforo", "invitado", "invitados", "visita", "visitas", "qr", "incidencia", "incidencias",
        "falla", "fuga", "gotera", "averia", "malogrado", "malograda", "no funciona", "luz", "agua", "desague",
        "humedad", "filtracion", "reparacion", "arreglar",
        // Plataforma
        "buildingfex", "comunicado", "comunicados", "aviso", "avisos", "documento", "documentos", "cuenta",
        "contrasena", "invitacion", "enlace", "app", "aplicacion",
        // English
        "building", "condo", "condominium", "apartment", "unit", "neighbor", "neighbour", "rules", "minutes",
        "board", "management", "manager", "concierge", "doorman", "pet", "pets", "dog", "cat", "noise", "parking",
        "trash", "garbage", "moving", "fee", "fees", "payment", "pay", "receipt", "debt", "dues", "booking",
        "reservation", "pool", "gym", "grill", "guest", "guests", "visitor", "incident", "leak", "broken",
        "elevator", "repair", "announcement", "password", "account",
    ];

    /// <summary>Pedidos claramente ajenos al edificio: se filtran aunque mencionen un tema del edificio.</summary>
    private static readonly string[] OffTopicRequests =
    [
        "chiste", "chistes", "poema", "poesia", "cancion", "cuento", "historia de amor", "receta", "recetas",
        "adivinanza", "trabalenguas", "horoscopo", "tarea", "ensayo", "monografia", "codigo", "programa en",
        "programar", "python", "javascript", "java ", "sql", "excel formula", "traduce", "traducir", "traduccion",
        "resume este libro", "escribe una carta", "ignora tus instrucciones", "ignora las instrucciones",
        "olvida tus instrucciones", "tu prompt", "system prompt", "que modelo eres", "eres chatgpt", "eres gemini",
        "joke", "poem", "song", "story about", "recipe", "riddle", "horoscope", "homework", "essay", "write code",
        "python", "translate", "ignore your instructions", "ignore previous instructions", "your prompt",
        "what model are you",
    ];

    /// <summary>Temas generales: se filtran solo si la consulta no menciona nada del edificio.</summary>
    private static readonly string[] GeneralTopics =
    [
        "clima", "pronostico", "futbol", "partido", "mundial", "pelicula", "peliculas", "serie", "series", "netflix",
        "noticias", "presidente", "politica", "elecciones", "bitcoin", "cripto", "criptomoneda", "dolar", "bolsa",
        "capital de", "quien gano", "quien es", "que es la", "historia del", "inteligencia artificial", "videojuego",
        "weather", "forecast", "football", "soccer", "movie", "netflix", "news", "president", "politics", "election",
        "bitcoin", "crypto", "stock market", "capital of", "who won", "who is", "video game",
    ];

    private static readonly string[] Greetings =
    [
        "hola", "holi", "buenas", "buenos dias", "buen dia", "buenas tardes", "buenas noches", "que tal", "hey",
        "hello", "hi", "good morning", "good afternoon", "good evening",
    ];

    private static readonly string[] Thanks =
    [
        "gracias", "muchas gracias", "mil gracias", "ok", "okey", "oki", "vale", "listo", "perfecto", "genial",
        "entendido", "de acuerdo", "chau", "chao", "adios", "hasta luego", "nos vemos", "bye", "thanks",
        "thank you", "thx", "great", "perfect", "got it", "goodbye", "see you",
    ];

    /// <summary>Una operación aritmética suelta (p. ej. "cuanto es 25*4"): no es una duda del edificio.</summary>
    private static readonly Regex Arithmetic = new(@"^(cuanto es |what is |calcula )?[\d\s.,]+[+\-*/x^][\d\s.,+\-*/x^()]+$",
        RegexOptions.Compiled);

    public static QuestionScope Classify(string text)
    {
        var folded = SupportEscalate.Fold(text);
        if (folded.Count(char.IsLetter) < 2)
            return QuestionScope.Unclear;

        var padded = " " + folded + " ";
        if (Arithmetic.IsMatch(folded))
            return QuestionScope.OffTopic;

        // Solo un saludo o un agradecimiento (pocas palabras y sin pregunta concreta).
        if (IsOnly(folded, Greetings) || IsOnly(folded, Thanks))
            return QuestionScope.SmallTalk;

        if (ContainsAny(padded, OffTopicRequests))
            return QuestionScope.OffTopic;

        var aboutBuilding = ContainsAny(padded, BuildingTerms);
        if (!aboutBuilding && ContainsAny(padded, GeneralTopics))
            return QuestionScope.OffTopic;

        return QuestionScope.Building;
    }

    /// <summary>¿El saludo o agradecimiento es de agradecimiento / despedida? (para elegir la respuesta).</summary>
    public static bool IsThanks(string text)
    {
        var folded = SupportEscalate.Fold(text);
        return IsOnly(folded, Thanks);
    }

    public static string SmallTalkMessage(string text, string locale)
    {
        var en = IsEnglish(locale);
        if (IsThanks(text))
        {
            return en
                ? "You're welcome! If you have another question about your building, just write it here."
                : "¡Con gusto! Si tienes otra duda sobre tu edificio, escríbela aquí.";
        }

        return en
            ? "Hi! I'm your building's assistant. I can help with the rules, payments, bookings, guests and incidents. What do you need?"
            : "¡Hola! Soy el asistente de tu edificio. Puedo ayudarte con el reglamento, los pagos, las reservas, tus invitados y las incidencias. ¿Qué necesitas?";
    }

    public static string OffTopicMessage(string locale) => IsEnglish(locale)
        ? "I can only help with questions about your building and BuildingFex: rules, meeting minutes, payments, bookings, guests or incidents. This message did not use any of your building's assistant queries."
        : "Solo puedo ayudarte con dudas de tu edificio y de BuildingFex: reglamento, actas, pagos, reservas, invitados o incidencias. Este mensaje no usó ninguna de las consultas del asistente de tu edificio.";

    public static string UnclearMessage(string locale) => IsEnglish(locale)
        ? "I didn't understand your message. Could you write your question about the building with a bit more detail?"
        : "No entendí tu mensaje. ¿Puedes escribir tu duda sobre el edificio con un poco más de detalle?";

    /// <summary>
    /// Pocas palabras y todas forman parte de un saludo / agradecimiento ("hola", "muchas gracias!", "ok listo").
    /// "Hola, ¿a qué hora abre la piscina?" no lo es: tiene contenido además del saludo.
    /// </summary>
    private static bool IsOnly(string folded, string[] phrases)
    {
        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 5) return false;
        var rest = " " + folded + " ";
        foreach (var phrase in phrases.OrderByDescending(p => p.Length))
            rest = rest.Replace(" " + phrase + " ", " ", StringComparison.Ordinal);
        foreach (var filler in new[] { "a", "y", "muy", "tu", "ti", "todo", "todos", "senor", "senora", "you", "so", "much", "very", "all" })
            rest = rest.Replace(" " + filler + " ", " ", StringComparison.Ordinal);
        return rest.Trim().Length == 0;
    }

    private static bool ContainsAny(string padded, string[] terms) =>
        terms.Any(t => padded.Contains(" " + t.Trim() + " ", StringComparison.Ordinal)
                       || (t.EndsWith(' ') && padded.Contains(" " + t, StringComparison.Ordinal)));

    private static bool IsEnglish(string locale) =>
        (locale ?? string.Empty).StartsWith("en", StringComparison.OrdinalIgnoreCase);
}
