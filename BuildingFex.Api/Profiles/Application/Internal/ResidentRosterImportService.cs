using System.Globalization;
using System.Net.Mail;
using BuildingFex.Api.Billing.Application.Internal;
using BuildingFex.Api.Iam.Application.CommandServices;
using BuildingFex.Api.Iam.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.Commands;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Profiles.Infrastructure.Excel;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;

namespace BuildingFex.Api.Profiles.Application.Internal;

/// <summary>Error de una fila (Row = número de fila de la hoja; 0 = error del archivo completo).</summary>
public sealed record RosterRowError(int Row, string? Column, string Code, string Message);

public sealed record RosterImportResult(
    bool Success,
    string? ErrorCode,
    string? Message,
    int TotalRows,
    IReadOnlyList<RosterRowError> Errors,
    IReadOnlyList<User> Created,
    int? PlanLimit = null,
    int? CurrentResidents = null);

/// <summary>
/// ImportRosterCommandHandler (informe 5.2.3) — US016: valida TODAS las filas primero; si alguna celda obligatoria
/// es inválida rechaza el archivo completo con el reporte por fila. Si todo es válido crea los residentes en una
/// sola transacción reutilizando CreateResidentCommand (token de invitación, límite del plan, etc.).
/// </summary>
public class ResidentRosterImportService(
    AppDbContext dbContext,
    IUserRepository userRepository,
    IUserCommandService userCommandService,
    ISubscriptionService subscriptionService,
    ILogger<ResidentRosterImportService> logger)
{
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy"];

    private sealed record ValidRow(
        int Row,
        string Name,
        string Floor,
        string Code,
        string? Email,
        string? AdmissionDate,
        bool BillFromNextMonth);

    public async Task<RosterImportResult> ImportAsync(User admin, RosterSheet sheet, CancellationToken ct = default)
    {
        var missing = new List<string>();
        if (!sheet.Columns.Contains(RosterColumns.Name)) missing.Add("nombre");
        if (!sheet.Columns.Contains(RosterColumns.Department)) missing.Add("departamento");
        if (missing.Count > 0)
            return FileError("MISSING_COLUMNS", $"Faltan columnas obligatorias: {string.Join(", ", missing)}.", 0);

        var dataRows = sheet.Rows.Where(r => !r.IsEmpty).ToList();
        if (dataRows.Count == 0)
            return FileError("EMPTY_FILE", "El archivo no tiene filas con residentes.", 0);

        var errors = new List<RosterRowError>();
        var valid = new List<ValidRow>();
        var codesInFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var emailsInFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in dataRows)
        {
            var rowErrors = new List<RosterRowError>();
            var name = row.Get(RosterColumns.Name).Trim();
            if (name.Length == 0)
                rowErrors.Add(new(row.RowNumber, "nombre", "NAME_REQUIRED", "El nombre es obligatorio."));
            else if (name.Length > 200)
                rowErrors.Add(new(row.RowNumber, "nombre", "NAME_TOO_LONG", "El nombre admite como máximo 200 caracteres."));

            string? floor = null;
            string? code = null;
            var departmentRaw = row.Get(RosterColumns.Department).Trim();
            if (departmentRaw.Length == 0)
            {
                rowErrors.Add(new(row.RowNumber, "departamento", "DEPARTMENT_REQUIRED", "El departamento es obligatorio."));
            }
            else if (!TryParseDepartment(departmentRaw, out code, out var derivedFloor))
            {
                rowErrors.Add(new(row.RowNumber, "departamento", "DEPARTMENT_INVALID",
                    $"\"{departmentRaw}\" no es válido: usa 3 o 4 dígitos (304 = piso 3, 1201 = piso 12)."));
            }
            else
            {
                floor = derivedFloor;
                var floorRaw = row.Get(RosterColumns.Floor).Trim();
                if (floorRaw.Length > 0)
                {
                    if (!int.TryParse(NormalizeNumber(floorRaw), NumberStyles.None, CultureInfo.InvariantCulture, out var floorNumber) || floorNumber <= 0)
                        rowErrors.Add(new(row.RowNumber, "piso", "FLOOR_INVALID", $"El piso \"{floorRaw}\" no es válido."));
                    else if (floorNumber.ToString(CultureInfo.InvariantCulture) != derivedFloor)
                        rowErrors.Add(new(row.RowNumber, "piso", "FLOOR_MISMATCH",
                            $"El piso {floorNumber} no coincide con el departamento {code} (piso {derivedFloor})."));
                }

                if (codesInFile.TryGetValue(code!, out var firstRow))
                    rowErrors.Add(new(row.RowNumber, "departamento", "DEPARTMENT_DUPLICATED_IN_FILE",
                        $"El departamento {code} ya aparece en la fila {firstRow}."));
                else
                    codesInFile[code!] = row.RowNumber;
            }

            string? email = null;
            var emailRaw = row.Get(RosterColumns.Email).Trim();
            if (emailRaw.Length > 0)
            {
                if (!IsValidEmail(emailRaw))
                {
                    rowErrors.Add(new(row.RowNumber, "email", "EMAIL_INVALID", $"\"{emailRaw}\" no es un email válido."));
                }
                else
                {
                    email = emailRaw.ToLowerInvariant();
                    if (emailsInFile.TryGetValue(email, out var firstEmailRow))
                        rowErrors.Add(new(row.RowNumber, "email", "EMAIL_DUPLICATED_IN_FILE",
                            $"El email {email} ya aparece en la fila {firstEmailRow}."));
                    else
                        emailsInFile[email] = row.RowNumber;
                }
            }

            string? admission = null;
            var dateRaw = row.Get(RosterColumns.AdmissionDate).Trim();
            if (dateRaw.Length > 0)
            {
                if (DateOnly.TryParseExact(dateRaw, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    admission = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                else
                    rowErrors.Add(new(row.RowNumber, "fecha_ingreso", "DATE_INVALID",
                        $"La fecha \"{dateRaw}\" no es válida (usa AAAA-MM-DD o DD/MM/AAAA)."));
            }

            var bill = false;
            var billRaw = row.Get(RosterColumns.BillFromNextMonth).Trim();
            if (billRaw.Length > 0 && !TryParseYesNo(billRaw, out bill))
                rowErrors.Add(new(row.RowNumber, "facturar_desde_mes_siguiente", "BILL_FLAG_INVALID",
                    $"\"{billRaw}\" no es válido: usa sí o no."));

            if (rowErrors.Count > 0)
            {
                errors.AddRange(rowErrors);
                continue;
            }

            valid.Add(new ValidRow(row.RowNumber, name, floor!, code!, email, admission, bill));
        }

        // Reglas contra la base: departamento ya registrado / email ya usado.
        foreach (var row in valid)
        {
            if (await userRepository.ExistsResidentByCodeAsync(row.Code, admin.Id, ct))
                errors.Add(new(row.Row, "departamento", "DEPARTMENT_ALREADY_EXISTS",
                    $"El departamento {row.Code} ya tiene un residente registrado."));
            if (row.Email is not null && await userRepository.ExistsByEmailAsync(row.Email, ct))
                errors.Add(new(row.Row, "email", "EMAIL_ALREADY_EXISTS", $"El email {row.Email} ya está registrado."));
        }

        if (errors.Count > 0)
        {
            return new RosterImportResult(
                false,
                "IMPORT_VALIDATION_FAILED",
                $"El archivo tiene {errors.Select(e => e.Row).Distinct().Count()} fila(s) con errores. No se importó ningún residente.",
                dataRows.Count,
                errors.OrderBy(e => e.Row).ToList(),
                []);
        }

        // RF028: el archivo completo debe entrar en el plan (y la prueba Free debe estar vigente).
        var gate = await subscriptionService.CheckRosterGrowthAsync(admin, valid.Count, ct);
        if (!gate.Allowed)
        {
            var message = gate.ErrorCode == SubscriptionService.ErrorPlanLimitReached
                ? $"El plan {gate.PlanId} permite {gate.Limit} departamentos; tienes {gate.CurrentCount} y el archivo agrega {valid.Count}. Mejora tu plan para importarlo."
                : gate.Message ?? "No se puede ampliar el padrón con la suscripción actual.";
            return new RosterImportResult(
                false,
                gate.ErrorCode,
                message,
                dataRows.Count,
                [new RosterRowError(0, null, gate.ErrorCode ?? "PLAN_LIMIT", message)],
                [],
                gate.Limit,
                gate.CurrentCount);
        }

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var created = new List<User>(valid.Count);
        await using var tx = await dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            for (var i = 0; i < valid.Count; i++)
            {
                var row = valid[i];
                var result = await userCommandService.Handle(
                    new CreateResidentCommand(
                        $"resident-{stamp}-{i + 1}",
                        row.Name,
                        row.Floor,
                        row.Code,
                        admin.ExternalId,
                        row.AdmissionDate,
                        row.BillFromNextMonth,
                        row.Email),
                    ct);

                if (result.IsFailure)
                {
                    await tx.RollbackAsync(ct);
                    dbContext.ChangeTracker.Clear();
                    var code = result.Error switch
                    {
                        IamError.ResidentCodeAlreadyExists => "DEPARTMENT_ALREADY_EXISTS",
                        IamError.ResidentPlanLimitReached => "RESIDENT_PLAN_LIMIT_REACHED",
                        IamError.SubscriptionTrialExpired => "SUBSCRIPTION_TRIAL_EXPIRED",
                        IamError.ResidentFieldsRequired => "RESIDENT_FIELDS_REQUIRED",
                        _ => "RESIDENT_CREATE_FAILED",
                    };
                    return new RosterImportResult(
                        false,
                        "IMPORT_VALIDATION_FAILED",
                        "No se importó ningún residente: una fila falló al guardarse.",
                        dataRows.Count,
                        [new RosterRowError(row.Row, null, code, result.Message ?? "No se pudo crear el residente.")],
                        []);
                }

                created.Add(result.Value!);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roster import failed for admin {AdminId}", admin.ExternalId);
            await tx.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            throw;
        }

        logger.LogInformation("Roster import: admin {AdminId} created {Count} residents", admin.ExternalId, created.Count);
        return new RosterImportResult(true, null, null, dataRows.Count, [], created);
    }

    private static RosterImportResult FileError(string code, string message, int totalRows) =>
        new(false, code, message, totalRows, [new RosterRowError(0, null, code, message)], []);

    /// <summary>Misma regla que el frontend (departmentNumber.js): 3–4 dígitos, piso = dígitos menos los 2 últimos.</summary>
    public static bool TryParseDepartment(string raw, out string? code, out string? floor)
    {
        code = null;
        floor = null;
        var digits = NormalizeNumber(raw);
        if (digits.Length is < 3 or > 4 || !digits.All(char.IsAsciiDigit)) return false;
        var floorNumber = int.Parse(digits[..^2], CultureInfo.InvariantCulture);
        if (floorNumber <= 0) return false;
        code = digits;
        floor = floorNumber.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>"304.0" (celda numérica de Excel) → "304"; quita espacios.</summary>
    private static string NormalizeNumber(string raw)
    {
        var value = raw.Trim().Replace(" ", string.Empty);
        if (value.EndsWith(".0", StringComparison.Ordinal)) value = value[..^2];
        return value;
    }

    private static bool IsValidEmail(string value)
    {
        if (value.Length > 320 || value.Contains(' ')) return false;
        try
        {
            var address = new MailAddress(value);
            return string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase)
                   && address.Host.Contains('.');
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseYesNo(string raw, out bool value)
    {
        var v = raw.Trim().ToLowerInvariant();
        if (v is "si" or "sí" or "s" or "yes" or "y" or "true" or "1" or "x") { value = true; return true; }
        if (v is "no" or "n" or "false" or "0") { value = false; return true; }
        value = false;
        return false;
    }
}
