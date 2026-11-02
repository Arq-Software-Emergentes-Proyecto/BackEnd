using System.Globalization;
using System.Text;
using BuildingFex.Api.Finances.Application.Internal.ReceiptDocuments;

namespace BuildingFex.Api.Finances.Infrastructure.Pdf;

/// <summary>
/// Generador PDF mínimo (PDF 1.4, Helvetica estándar, WinAnsiEncoding) para el comprobante de pago.
/// Sin dependencias nativas ni licencias de terceros: funciona igual en Railway/Docker que en local.
/// </summary>
public class SimpleReceiptPdfRenderer : IReceiptPdfRenderer
{
    private const float PageWidth = 595f;  // A4
    private const float PageHeight = 842f;
    private const float Margin = 56f;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public byte[] Render(ReceiptPdfModel m)
    {
        var c = new StringBuilder();
        var right = PageWidth - Margin;
        var y = PageHeight - Margin;

        // Banda superior
        Fill(c, 0.12f, 0.23f, 0.54f);
        Rect(c, 0, PageHeight - 96, PageWidth, 96);
        c.Append("f\n");
        Fill(c, 1, 1, 1);
        Text(c, "F2", 20, Margin, PageHeight - 52, "BuildingFex");
        Text(c, "F1", 10, Margin, PageHeight - 70, "Comprobante de pago de cuota de mantenimiento");
        TextRight(c, "F2", 11, right, PageHeight - 52, $"N.° {m.Number}");
        TextRight(c, "F1", 9, right, PageHeight - 70, $"Emitido: {m.IssuedAt}");

        y = PageHeight - 130;
        Fill(c, 0.11f, 0.11f, 0.12f);
        Text(c, "F2", 12, Margin, y, m.BuildingName);
        y -= 15;
        Fill(c, 0.43f, 0.43f, 0.45f);
        if (!string.IsNullOrWhiteSpace(m.BuildingRuc)) { Text(c, "F1", 9, Margin, y, $"RUC: {m.BuildingRuc}"); y -= 12; }
        if (!string.IsNullOrWhiteSpace(m.BuildingAddress)) { Text(c, "F1", 9, Margin, y, m.BuildingAddress!); y -= 12; }

        // Sello PAGADO
        Stroke(c, 0.13f, 0.55f, 0.27f);
        c.Append("1.5 w\n");
        Rect(c, right - 110, PageHeight - 150, 110, 34);
        c.Append("S\n");
        Fill(c, 0.13f, 0.55f, 0.27f);
        TextCenter(c, "F2", 14, right - 55, PageHeight - 138, "PAGADO");

        y -= 18;
        Separator(c, y);
        y -= 24;

        Fill(c, 0.11f, 0.11f, 0.12f);
        y = Field(c, y, "Residente", m.ResidentName);
        if (!string.IsNullOrWhiteSpace(m.ResidentUnit)) y = Field(c, y, "Unidad", m.ResidentUnit!);
        if (!string.IsNullOrWhiteSpace(m.ResidentEmail)) y = Field(c, y, "Correo", m.ResidentEmail!);
        y = Field(c, y, "Concepto", m.Concept);
        if (!string.IsNullOrWhiteSpace(m.BillingMonth)) y = Field(c, y, "Periodo", m.BillingMonth!);
        if (!string.IsNullOrWhiteSpace(m.DueDate)) y = Field(c, y, "Vencimiento", m.DueDate!);
        y = Field(c, y, "Fecha de pago", m.PaidAt);
        y = Field(c, y, "Medio de pago", m.Method);
        if (!string.IsNullOrWhiteSpace(m.Reference)) y = Field(c, y, "Referencia", m.Reference!);

        y -= 10;
        // Tabla de detalle
        Fill(c, 0.96f, 0.96f, 0.97f);
        Rect(c, Margin, y - 8, right - Margin, 24);
        c.Append("f\n");
        Fill(c, 0.43f, 0.43f, 0.45f);
        Text(c, "F2", 9, Margin + 10, y, "DETALLE");
        TextRight(c, "F2", 9, right - 10, y, $"IMPORTE ({m.Currency})");
        y -= 28;

        Fill(c, 0.11f, 0.11f, 0.12f);
        foreach (var line in m.Lines)
        {
            Text(c, "F1", 10, Margin + 10, y, line.Label);
            TextRight(c, "F1", 10, right - 10, y, Money(line.Amount));
            y -= 20;
        }

        Separator(c, y + 8);
        y -= 10;
        Text(c, "F2", 12, Margin + 10, y, "Total pagado");
        TextRight(c, "F2", 12, right - 10, y, $"S/ {Money(m.Total)}");

        // Pie
        Fill(c, 0.53f, 0.53f, 0.55f);
        Text(c, "F1", 8, Margin, Margin + 12,
            "Documento generado electrónicamente por BuildingFex. Constancia de pago interna del condominio;");
        Text(c, "F1", 8, Margin, Margin,
            "no reemplaza comprobantes tributarios SUNAT.");

        return BuildDocument(Encode(c.ToString()));
    }

    private static float Field(StringBuilder c, float y, string label, string value)
    {
        Fill(c, 0.43f, 0.43f, 0.45f);
        Text(c, "F1", 9, Margin, y, label);
        Fill(c, 0.11f, 0.11f, 0.12f);
        Text(c, "F2", 10, Margin + 110, y, Truncate(value, 70));
        return y - 17;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string Money(decimal amount) => amount.ToString("N2", Inv);

    private static void Separator(StringBuilder c, float y)
    {
        Stroke(c, 0.85f, 0.85f, 0.87f);
        c.Append("0.75 w\n");
        c.Append(F(Margin)).Append(' ').Append(F(y)).Append(" m ")
            .Append(F(PageWidth - Margin)).Append(' ').Append(F(y)).Append(" l S\n");
    }

    private static void Fill(StringBuilder c, float r, float g, float b) =>
        c.Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append(" rg\n");

    private static void Stroke(StringBuilder c, float r, float g, float b) =>
        c.Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append(" RG\n");

    private static void Rect(StringBuilder c, float x, float y, float w, float h) =>
        c.Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(w)).Append(' ').Append(F(h)).Append(" re\n");

    private static void Text(StringBuilder c, string font, float size, float x, float y, string text) =>
        c.Append("BT /").Append(font).Append(' ').Append(F(size)).Append(" Tf ")
            .Append(F(x)).Append(' ').Append(F(y)).Append(" Td (").Append(Escape(text)).Append(") Tj ET\n");

    private static void TextRight(StringBuilder c, string font, float size, float right, float y, string text) =>
        Text(c, font, size, right - Width(text, size, font == "F2"), y, text);

    private static void TextCenter(StringBuilder c, string font, float size, float center, float y, string text) =>
        Text(c, font, size, center - Width(text, size, font == "F2") / 2, y, text);

    /// <summary>Ancho aproximado con métricas medias de Helvetica (suficiente para alinear a la derecha).</summary>
    private static float Width(string text, float size, bool bold)
    {
        float units = 0;
        foreach (var ch in text)
        {
            units += ch switch
            {
                ' ' => 278,
                '.' or ',' or ':' => 278,
                >= '0' and <= '9' => 556,
                'i' or 'l' or 'j' or 'I' or '|' => 260,
                'm' or 'w' or 'M' or 'W' => 833,
                >= 'A' and <= 'Z' => bold ? 722 : 667,
                _ => bold ? 590 : 556,
            };
        }
        return units * size / 1000f;
    }

    private static string F(float v) => v.ToString("0.##", Inv);

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (ch is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>UTF-16 → WinAnsi (CP-1252): tildes y ñ incluidas; lo no representable se reemplaza por '?'.</summary>
    private static byte[] Encode(string s)
    {
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            bytes[i] = ch switch
            {
                '€' => 0x80,
                '‚' => 0x82,
                '…' => 0x85,
                '‘' => 0x91,
                '’' => 0x92,
                '“' => 0x93,
                '”' => 0x94,
                '•' => 0x95,
                '–' => 0x96,
                '—' => 0x97,
                _ when ch < 0x80 || (ch >= 0xA0 && ch <= 0xFF) => (byte)ch,
                _ => (byte)'?',
            };
        }
        return bytes;
    }

    private static byte[] BuildDocument(byte[] content)
    {
        using var ms = new MemoryStream();
        var offsets = new List<long>();

        void Write(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        void BeginObj()
        {
            offsets.Add(ms.Position);
            Write($"{offsets.Count} 0 obj\n");
        }

        Write("%PDF-1.4\n");
        ms.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // comentario binario: marca el archivo como binario

        BeginObj(); Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(); Write("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj();
        Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}] " +
              "/Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>\nendobj\n");
        BeginObj(); Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        BeginObj(); Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
        BeginObj();
        Write($"<< /Length {content.Length} >>\nstream\n");
        ms.Write(content, 0, content.Length);
        Write("\nendstream\nendobj\n");
        BeginObj();
        Write("<< /Producer (BuildingFex) /Title (Comprobante de pago) >>\nendobj\n");

        var xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            Write($"{off:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info {offsets.Count} 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }
}
