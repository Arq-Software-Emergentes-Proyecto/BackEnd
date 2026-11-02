using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace BuildingFex.Api.Profiles.Infrastructure.Excel;

/// <summary>Columnas canónicas del padrón (plantilla de importación, US016).</summary>
public static class RosterColumns
{
    public const string Name = "name";
    public const string Department = "department";
    public const string Floor = "floor";
    public const string Email = "email";
    public const string AdmissionDate = "admissionDate";
    public const string BillFromNextMonth = "billFromNextMonth";

    /// <summary>Encabezados de la plantilla, en orden.</summary>
    public static IReadOnlyList<(string Key, string Header, bool Required)> Template { get; } =
    [
        (Name, "nombre", true),
        (Department, "departamento", true),
        (Floor, "piso", false),
        (Email, "email", false),
        (AdmissionDate, "fecha_ingreso", false),
        (BillFromNextMonth, "facturar_desde_mes_siguiente", false),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["nombre"] = Name, ["nombres"] = Name, ["nombrecompleto"] = Name, ["nombreyapellido"] = Name,
        ["nombresyapellidos"] = Name, ["residente"] = Name, ["vecino"] = Name, ["name"] = Name, ["fullname"] = Name,

        ["departamento"] = Department, ["depto"] = Department, ["dpto"] = Department, ["dep"] = Department,
        ["numerodepartamento"] = Department, ["nrodepartamento"] = Department, ["unidad"] = Department,
        ["codigo"] = Department, ["department"] = Department, ["apartment"] = Department, ["unit"] = Department,
        ["code"] = Department,

        ["piso"] = Floor, ["floor"] = Floor,

        ["email"] = Email, ["correo"] = Email, ["correoelectronico"] = Email, ["mail"] = Email, ["emailaddress"] = Email,

        ["fechaingreso"] = AdmissionDate, ["fechadeingreso"] = AdmissionDate, ["ingreso"] = AdmissionDate,
        ["admissiondate"] = AdmissionDate, ["startdate"] = AdmissionDate, ["moveindate"] = AdmissionDate,

        ["facturardesdemessiguiente"] = BillFromNextMonth, ["facturarmessiguiente"] = BillFromNextMonth,
        ["billfromnextmonth"] = BillFromNextMonth,
    };

    /// <summary>"Fecha de ingreso" → "admissionDate"; null si la columna no se reconoce.</summary>
    public static string? Resolve(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var normalized = header.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        var key = sb.ToString();
        // "Fecha de ingreso (AAAA-MM-DD)" → "fechadeingresoaaaammdd": quitar sufijos de formato conocidos.
        foreach (var suffix in new[] { "aaaammdd", "ddmmaaaa", "yyyymmdd", "opcional", "obligatorio", "sino" })
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal) && key.Length > suffix.Length)
                key = key[..^suffix.Length];
        }
        return Aliases.TryGetValue(key, out var canonical) ? canonical : null;
    }
}

/// <summary>Fila leída: número de fila de la hoja (1 = encabezado) y celdas por columna canónica.</summary>
public sealed record RosterRawRow(int RowNumber, IReadOnlyDictionary<string, string> Cells)
{
    public string Get(string column) => Cells.TryGetValue(column, out var value) ? value : string.Empty;

    public bool IsEmpty => Cells.Values.All(string.IsNullOrWhiteSpace);
}

public sealed record RosterSheet(IReadOnlyList<string> Columns, IReadOnlyList<RosterRawRow> Rows);

public class RosterFileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Adapter (informe 5.2.4 — ExcelRosterReader): lee .xlsx (ClosedXML) o .csv (coma o punto y coma) a filas
/// con columnas canónicas. No valida reglas de negocio; eso lo hace ResidentRosterImportService.
/// </summary>
public static class ExcelRosterReader
{
    public const long MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxRows = 1000;

    public static RosterSheet Read(Stream stream, string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" or ".xlsm" => ReadXlsx(stream),
            ".csv" or ".txt" => ReadCsv(stream),
            ".xls" => throw new RosterFileException(
                "UNSUPPORTED_FILE_TYPE",
                "El formato .xls (Excel 97-2003) no es compatible. Guarda el archivo como .xlsx o .csv."),
            _ => throw new RosterFileException(
                "UNSUPPORTED_FILE_TYPE",
                "Sube un archivo .xlsx o .csv (usa la plantilla)."),
        };
    }

    private static RosterSheet ReadXlsx(Stream stream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex)
        {
            throw new RosterFileException("INVALID_FILE", $"No se pudo leer el Excel: {ex.Message}");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(ws => ws.LastRowUsed() is not null)
                ?? throw new RosterFileException("EMPTY_FILE", "El archivo no tiene datos.");

            var lastRow = sheet.LastRowUsed()!.RowNumber();
            var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

            // Encabezado = primera fila con alguna columna reconocida (la plantilla puede traer un título encima).
            int headerRow = -1;
            Dictionary<int, string> map = new();
            for (var r = 1; r <= Math.Min(lastRow, 10) && headerRow < 0; r++)
            {
                var candidate = new Dictionary<int, string>();
                for (var c = 1; c <= lastColumn; c++)
                {
                    var key = RosterColumns.Resolve(sheet.Cell(r, c).GetString());
                    if (key is not null && !candidate.ContainsValue(key)) candidate[c] = key;
                }
                if (candidate.ContainsValue(RosterColumns.Name) || candidate.ContainsValue(RosterColumns.Department))
                {
                    headerRow = r;
                    map = candidate;
                }
            }

            if (headerRow < 0)
                throw new RosterFileException(
                    "MISSING_COLUMNS",
                    "No se encontraron las columnas \"nombre\" y \"departamento\". Usa la plantilla.");

            if (lastRow - headerRow > MaxRows)
                throw new RosterFileException("TOO_MANY_ROWS", $"El archivo supera el máximo de {MaxRows} filas.");

            var rows = new List<RosterRawRow>();
            for (var r = headerRow + 1; r <= lastRow; r++)
            {
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (column, key) in map)
                    cells[key] = CellText(sheet.Cell(r, column));
                rows.Add(new RosterRawRow(r, cells));
            }

            return new RosterSheet(map.Values.ToList(), rows);
        }
    }

    private static string CellText(IXLCell cell)
    {
        if (cell.IsEmpty()) return string.Empty;
        try
        {
            return cell.DataType switch
            {
                XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                XLDataType.Number => cell.GetDouble().ToString("0.##########", CultureInfo.InvariantCulture),
                XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
                _ => cell.GetFormattedString().Trim(),
            };
        }
        catch
        {
            return cell.GetFormattedString().Trim();
        }
    }

    private static RosterSheet ReadCsv(Stream stream)
    {
        string text;
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();

        var lines = SplitCsvRecords(text);
        var firstIndex = lines.FindIndex(l => !string.IsNullOrWhiteSpace(l.Text));
        if (firstIndex < 0)
            throw new RosterFileException("EMPTY_FILE", "El archivo no tiene datos.");

        var headerLine = lines[firstIndex].Text;
        var delimiter = headerLine.Count(ch => ch == ';') > headerLine.Count(ch => ch == ',') ? ';' : ',';
        var headers = ParseCsvLine(headerLine, delimiter);
        var map = new Dictionary<int, string>();
        for (var i = 0; i < headers.Count; i++)
        {
            var key = RosterColumns.Resolve(headers[i]);
            if (key is not null && !map.ContainsValue(key)) map[i] = key;
        }

        if (!map.ContainsValue(RosterColumns.Name) && !map.ContainsValue(RosterColumns.Department))
            throw new RosterFileException(
                "MISSING_COLUMNS",
                "No se encontraron las columnas \"nombre\" y \"departamento\". Usa la plantilla.");

        if (lines.Count - firstIndex - 1 > MaxRows)
            throw new RosterFileException("TOO_MANY_ROWS", $"El archivo supera el máximo de {MaxRows} filas.");

        var rows = new List<RosterRawRow>();
        foreach (var (lineNumber, lineText) in lines.Skip(firstIndex + 1))
        {
            var values = ParseCsvLine(lineText, delimiter);
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (index, key) in map)
                cells[key] = index < values.Count ? values[index].Trim() : string.Empty;
            rows.Add(new RosterRawRow(lineNumber, cells));
        }

        return new RosterSheet(map.Values.ToList(), rows);
    }

    /// <summary>Registros CSV (respeta saltos de línea dentro de comillas) con su número de línea inicial.</summary>
    private static List<(int LineNumber, string Text)> SplitCsvRecords(string text)
    {
        var records = new List<(int, string)>();
        var current = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var startLine = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"') inQuotes = !inQuotes;
            if (!inQuotes && (ch == '\n' || ch == '\r'))
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                records.Add((startLine, current.ToString()));
                current.Clear();
                line++;
                startLine = line;
                continue;
            }
            if (ch == '\n') line++;
            current.Append(ch);
        }
        if (current.Length > 0) records.Add((startLine, current.ToString()));
        return records;
    }

    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        var values = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == delimiter) { values.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        values.Add(sb.ToString());
        return values;
    }

    /// <summary>Plantilla .xlsx: hoja "Residentes" (encabezados + 2 ejemplos) y hoja "Instrucciones".</summary>
    public static byte[] BuildXlsxTemplate()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Residentes");
        for (var i = 0; i < RosterColumns.Template.Count; i++)
        {
            var (_, header, required) = RosterColumns.Template[i];
            var cell = sheet.Cell(1, i + 1);
            cell.Value = header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = required ? XLColor.FromHtml("#DCEBFF") : XLColor.FromHtml("#F2F2F2");
        }
        object[][] examples =
        [
            ["Ana Torres", "304", "3", "ana.torres@correo.com", "2026-01-15", "no"],
            ["Luis Ramos", "1201", "", "", "", ""],
        ];
        for (var r = 0; r < examples.Length; r++)
            for (var c = 0; c < examples[r].Length; c++)
                sheet.Cell(r + 2, c + 1).Value = examples[r][c]?.ToString() ?? string.Empty;
        // Departamento como texto para conservar ceros y evitar "304.0".
        sheet.Column(2).Style.NumberFormat.Format = "@";
        sheet.Columns().AdjustToContents();

        var help = workbook.Worksheets.Add("Instrucciones");
        string[] lines =
        [
            "Plantilla de importación de residentes — BuildingFex",
            "",
            "nombre (obligatorio): nombre completo del residente.",
            "departamento (obligatorio): número de 3 o 4 dígitos; los 2 últimos son la unidad y los primeros el piso (304 = piso 3, 1201 = piso 12).",
            "piso (opcional): si lo completas debe coincidir con el departamento.",
            "email (opcional): si lo completas, la invitación solo podrá aceptarse con ese correo.",
            "fecha_ingreso (opcional): AAAA-MM-DD o DD/MM/AAAA. Si falta se usa la fecha de hoy.",
            "facturar_desde_mes_siguiente (opcional): sí / no.",
            "",
            "Si alguna fila tiene errores, el archivo completo se rechaza y se muestra el detalle por fila; corrígelo y vuelve a subirlo.",
            "Cada residente creado recibe un enlace de invitación (válido 7 días) que debes compartirle.",
        ];
        for (var i = 0; i < lines.Length; i++) help.Cell(i + 1, 1).Value = lines[i];
        help.Cell(1, 1).Style.Font.Bold = true;
        help.Column(1).Width = 120;

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    public static byte[] BuildCsvTemplate()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', RosterColumns.Template.Select(t => t.Header)));
        sb.AppendLine("Ana Torres;304;3;ana.torres@correo.com;2026-01-15;no");
        sb.AppendLine("Luis Ramos;1201;;;;");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}
