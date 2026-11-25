using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

namespace BuildingFex.Api.ResidentCare.Domain.Services;

/// <summary>
/// Guía de uso de BuildingFex (documento fijo del producto). Junto con los documentos vigentes
/// del edificio es la única base documental del asistente: se recupera por palabra clave y se cita
/// como fuente igual que un reglamento o un acta.
/// </summary>
public static class AppUsageGuide
{
    public const string DocumentIdPrefix = "app-guide:";
    public const string FileNameEs = "Guía de uso de BuildingFex";
    public const string FileNameEn = "BuildingFex user guide";

    private sealed record Section(string Id, string[] Keywords, string Content);

    private static readonly Section[] Sections =
    [
        new("finanzas",
        [
            "pagar", "pago", "pagos", "cuota", "cuotas", "mantenimiento", "recibo", "recibos", "deuda", "saldo",
            "estado de cuenta", "mercado pago", "tarjeta", "finanzas", "conciliacion", "comprobante", "morosidad",
            "pay", "payment", "payments", "fee", "fees", "receipt", "receipts", "balance", "debt", "dues",
        ],
        "Finanzas (menú Finanzas): el residente ve su estado de cuenta, las cuotas pendientes y sus recibos. " +
        "Para pagar: Finanzas → selecciona las cuotas pendientes → Pagar; el pago se procesa en Mercado Pago. " +
        "Si Mercado Pago rechaza o cancelas el pago, la deuda se mantiene y puedes reintentar. " +
        "Los recibos de cuotas pagadas se consultan y descargan en Finanzas. " +
        "El asistente no puede ver, confirmar ni modificar saldos o pagos: el dato oficial está en Finanzas."),
        new("servicios",
        [
            "reservar", "reserva", "reservas", "area comun", "areas comunes", "piscina", "parrilla", "gimnasio",
            "salon", "sum", "cancha", "aforo", "cancelar reserva", "servicios", "disponibilidad",
            "reserve", "reservation", "booking", "book", "common area", "common areas", "pool", "gym", "grill",
        ],
        "Servicios (menú Servicios): permite reservar áreas comunes. Servicios → elige el área → fecha y horario " +
        "disponible → confirmar. El sistema no permite reservas que se traslapen ni que superen el aforo del área. " +
        "Las reservas propias se ven y cancelan desde Servicios. Los horarios, costos y reglas de uso de cada área " +
        "los fija el reglamento del edificio."),
        new("generacion",
        [
            "invitado", "invitados", "visita", "visitas", "qr", "codigo qr", "generacion", "acceso", "ingreso",
            "guest", "guests", "visitor", "visitors", "qr code", "invite",
        ],
        "Generación (menú Generación): genera un código QR para tus invitados (por ejemplo, de una reserva). " +
        "Comparte el QR con el invitado; el personal del edificio lo valida al ingresar."),
        new("incidencias",
        [
            "incidencia", "incidencias", "reportar", "reporte", "falla", "fallas", "fuga", "gotera", "averia",
            "roto", "rota", "malogrado", "malograda", "no funciona", "ascensor", "luz", "agua", "desague", "ruido",
            "dano", "danos", "problema", "reparacion", "arreglar", "humedad", "filtracion",
            "issue", "incident", "report", "broken", "leak", "not working", "noise", "repair", "damage", "elevator",
        ],
        "Incidencias (menú Incidencias): para reportar un problema del edificio o de tu departamento: " +
        "Incidencias → Nueva incidencia → describe qué ocurre, dónde (torre, piso, departamento o área común), " +
        "desde cuándo y si es urgente o afecta a otros vecinos; adjunta una foto si puedes → Enviar. " +
        "Puedes seguir el estado (abierta, en progreso, resuelta), el proveedor asignado y los comentarios. " +
        "Datos mínimos para una incidencia útil: ubicación exacta, descripción del problema y nivel de urgencia."),
        new("comunicados",
        [
            "comunicado", "comunicados", "aviso", "avisos", "anuncio", "anuncios", "dashboard", "inicio", "noticias",
            "announcement", "announcements", "notice", "notices", "news",
        ],
        "Dashboard (menú Dashboard): muestra el resumen de tu estado de cuenta, tus próximas reservas y los " +
        "comunicados vigentes que publica la administración del edificio."),
        new("cuenta",
        [
            "cuenta", "contrasena", "perfil", "idioma", "configuracion", "ajustes", "settings", "sesion",
            "iniciar sesion", "login", "google", "invitacion", "enlace", "correo",
            "account", "password", "language", "profile", "sign in", "log in", "invitation", "link", "email",
        ],
        "Cuenta: los residentes ingresan con Google (o correo) desde el enlace de invitación que envía la " +
        "administración; si el enlace venció, pide uno nuevo a la administración. En Settings puedes cambiar el " +
        "idioma (español / inglés) y revisar tu perfil."),
        new("soporte",
        [
            "soporte", "preguntas frecuentes", "faq", "contacto", "contactar", "derivar",
            "support", "contact", "frequently asked questions",
        ],
        "Soporte (menú Soporte): preguntas frecuentes y este chat. El asistente responde con el reglamento y las " +
        "actas vigentes del edificio; si no encuentra base documental o pides hablar con una persona, deriva la " +
        "consulta a la junta o a la administración, que responde en el mismo chat."),
    ];

    public static string FileName(string locale) =>
        locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? FileNameEn : FileNameEs;

    /// <summary>Secciones cuya palabra clave aparece en la consulta (orden por número de coincidencias).</summary>
    public static IReadOnlyList<KnowledgeSource> Match(string query, string locale, int max = 2)
    {
        var folded = " " + SupportEscalate.Fold(query) + " ";
        if (folded.Trim().Length == 0) return [];

        return Sections
            .Select(s => (Section: s, Hits: s.Keywords.Count(k => folded.Contains(" " + k + " ", StringComparison.Ordinal))))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .Take(Math.Max(1, max))
            .Select(x => new KnowledgeSource(
                DocumentIdPrefix + x.Section.Id,
                FileName(locale),
                x.Section.Content,
                Math.Min(1.0, 0.5 + 0.1 * x.Hits)))
            .ToList();
    }
}
