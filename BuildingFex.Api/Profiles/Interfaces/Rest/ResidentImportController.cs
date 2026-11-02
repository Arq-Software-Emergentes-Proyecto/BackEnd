using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Iam.Interfaces.Rest.Transform;
using BuildingFex.Api.Profiles.Application.Internal;
using BuildingFex.Api.Profiles.Infrastructure.Excel;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingFex.Api.Profiles.Interfaces.Rest;

/// <summary>
/// Importación masiva del padrón desde Excel/CSV (RF016 / US016). Solo admin; el tenant sale del token.
/// No hay proveedor de correo integrado: la respuesta trae los tokens de invitación para que el admin
/// comparta los enlaces (copiar / exportar) en lugar de enviar correos de bienvenida.
/// </summary>
[ApiController]
[Route("api/v1/residents/import")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class ResidentImportController(ResidentRosterImportService importService) : TenantScopedControllerBase
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>multipart/form-data con el campo <c>file</c> (.xlsx o .csv, máx. 2 MB / 1000 filas).</summary>
    [HttpPost]
    [RequestSizeLimit(ExcelRosterReader.MaxFileBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ExcelRosterReader.MaxFileBytes + 64 * 1024)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(null, ct);
        if (tenant.Error is not null) return tenant.Error;
        var caller = tenant.Caller!;
        if (!caller.IsAdmin)
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "ADMIN_REQUIRED", message = "Solo administradores." });

        if (file is null || file.Length == 0)
            return BadRequest(FileErrorBody("FILE_REQUIRED", "Selecciona un archivo .xlsx o .csv."));
        if (file.Length > ExcelRosterReader.MaxFileBytes)
            return BadRequest(FileErrorBody("FILE_TOO_LARGE", "El archivo supera el máximo de 2 MB."));

        RosterSheet sheet;
        try
        {
            await using var stream = file.OpenReadStream();
            // ClosedXML necesita un stream con Seek.
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            sheet = ExcelRosterReader.Read(buffer, file.FileName);
        }
        catch (RosterFileException ex)
        {
            return BadRequest(FileErrorBody(ex.Code, ex.Message));
        }

        var result = await importService.ImportAsync(caller.TenantAdmin, sheet, ct);
        if (!result.Success)
        {
            return BadRequest(new
            {
                code = result.ErrorCode ?? "IMPORT_VALIDATION_FAILED",
                message = result.Message,
                totalRows = result.TotalRows,
                invalidRows = result.Errors.Where(e => e.Row > 0).Select(e => e.Row).Distinct().Count(),
                residentLimit = result.PlanLimit,
                residentsCount = result.CurrentResidents,
                errors = result.Errors.Select(e => new { row = e.Row, column = e.Column, code = e.Code, message = e.Message }),
            });
        }

        return StatusCode(StatusCodes.Status201Created, new
        {
            created = result.Created.Count,
            totalRows = result.TotalRows,
            residents = result.Created.Select(UserResourceAssembler.ToResource),
        });
    }

    /// <summary>Plantilla descargable: ?format=xlsx (por defecto) o ?format=csv.</summary>
    [HttpGet("template")]
    public IActionResult Template([FromQuery] string? format)
    {
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return File(ExcelRosterReader.BuildCsvTemplate(), "text/csv; charset=utf-8", "plantilla-residentes.csv");
        return File(ExcelRosterReader.BuildXlsxTemplate(), XlsxContentType, "plantilla-residentes.xlsx");
    }

    private static object FileErrorBody(string code, string message) => new
    {
        code,
        message,
        totalRows = 0,
        invalidRows = 0,
        errors = new[] { new { row = 0, column = (string?)null, code, message } },
    };
}
