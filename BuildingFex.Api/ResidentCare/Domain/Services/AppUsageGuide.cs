using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

namespace BuildingFex.Api.ResidentCare.Domain.Services;

/// <summary>Guía fija del producto BuildingFex; también se cita como fuente en las respuestas.</summary>
public static class AppUsageGuide
{
    public const string DocumentIdPrefix = "app-guide:";
    public const string FileNameEs = "Guía de uso de BuildingFex";
    public const string FileNameEn = "BuildingFex user guide";

    private sealed record GuideSection(string Id, string[] Keywords, string Content);

    private static readonly IReadOnlyList<GuideSection> Sections =
    [
        new(
            "finanzas",
            [
                "finanzas", "cuota", "cuotas", "pago", "pagar", "recibo", "recibos", "estado de cuenta",
                "saldo", "deuda", "mora", "mercado pago", "mercadopago",
                "finance", "finances", "fee", "fees", "pay", "payment", "payments", "receipt", "receipts", "balance", "debt", "statement",
            ],
            "En la sección Finanzas puedes revisar tu estado de cuenta, tus cuotas pendientes y tus recibos emitidos. Para pagar una cuota selecciona los conceptos pendientes y continúa con Mercado Pago. El asistente no tiene acceso a tus saldos ni pagos individuales, así que verifica los montos directamente en Finanzas."),
        new(
            "servicios",
            [
                "servicio", "servicios", "reserva", "reservar", "reservas", "area comun", "areas comunes",
                "aforo", "cancelar", "cancelacion", "piscina", "salon", "parrilla", "gimnasio",
                "service", "services", "reserve", "reservation", "reservations", "book", "booking", "common area", "amenity", "amenities", "capacity", "cancel",
            ],
            "En Servicios puedes consultar las áreas comunes disponibles y crear una reserva eligiendo fecha y horario. El sistema evita traslapes de horarios y controla que no se exceda el aforo permitido. Si necesitas cancelar una reserva confirmada, puedes hacerlo desde el listado de tus reservas."),
        new(
            "generacion",
            [
                "qr", "invitado", "invitados", "visita", "visitas", "pase", "pases", "codigo", "acceso", "generacion",
                "guest", "guests", "visitor", "visitors", "pass", "passes", "code", "access",
            ],
            "Para generar un pase QR para invitados ingresa a la sección de generación de pases o reservas con invitados, registra los datos de la visita y comparte el código QR generado. El pase tiene vigencia temporal para validar el ingreso en portería."),
        new(
            "incidencias",
            [
                "incidencia", "incidencias", "reportar", "reporte", "nueva incidencia", "fuga", "averia",
                "dano", "urgencia", "foto", "reclamo", "problema",
                "incident", "incidents", "report", "leak", "damage", "urgency", "photo", "issue", "maintenance",
            ],
            "Para reportar un problema entra a Incidencias y selecciona Nueva incidencia. Indica qué está ocurriendo, dónde se ubica, desde cuándo sucede, el nivel de urgencia y adjunta una foto si ayuda a identificarlo. Desde la misma pantalla puedes seguir el estado del reporte hasta su resolución."),
        new(
            "comunicados",
            [
                "comunicado", "comunicados", "aviso", "avisos", "anuncio", "anuncios", "dashboard", "noticias",
                "announcement", "announcements", "notice", "notices", "news",
            ],
            "Los comunicados oficiales publicados por la administración o la junta aparecen en el Dashboard principal. Allí puedes consultar avisos vigentes ordenados por prioridad, como urgentes, importantes y normales."),
        new(
            "cuenta",
            [
                "cuenta", "perfil", "ingreso", "ingresar", "login", "google", "correo", "invitacion",
                "contrasena", "clave", "settings", "configuracion", "idioma", "espanol", "ingles",
                "account", "profile", "sign in", "email", "invite", "invitation", "password", "language",
            ],
            "Puedes ingresar a BuildingFex con Google o con tu correo y contraseña activando tu cuenta desde la invitación enviada por tu administración. En Settings puedes revisar tus datos de perfil y cambiar el idioma de la interfaz entre español e inglés."),
        new(
            "soporte",
            [
                "soporte", "ayuda", "faq", "preguntas frecuentes", "chat", "asistente", "administracion", "derivar",
                "support", "help", "assistant", "administration", "escalate",
            ],
            "En Soporte cuentas con preguntas frecuentes (FAQ) y este chat con el asistente del edificio. Si tu consulta requiere atención humana o no existe base documental en el reglamento ni en las actas, la conversación puede derivarse a la administración."),
    ];

    public static string FileName(string locale) =>
        locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? FileNameEn : FileNameEs;

    public static IReadOnlyList<KnowledgeSource> Match(string query, string locale, int max = 2)
    {
        if (max <= 0) return [];
        var folded = SupportEscalate.Fold(query);
        if (folded.Length == 0) return [];

        var padded = $" {folded} ";
        var fileName = FileName(locale);

        var matches = new List<(KnowledgeSource Source, int Hits)>();
        foreach (var section in Sections)
        {
            var hits = 0;
            foreach (var kw in section.Keywords)
            {
                if (padded.Contains($" {kw} ", StringComparison.Ordinal))
                    hits++;
            }

            if (hits == 0) continue;

            var score = Math.Min(1.0, 0.5 + 0.1 * hits);
            matches.Add((new KnowledgeSource(DocumentIdPrefix + section.Id, fileName, section.Content, score), hits));
        }

        return matches
            .OrderByDescending(m => m.Hits)
            .Take(max)
            .Select(m => m.Source)
            .ToList();
    }
}
