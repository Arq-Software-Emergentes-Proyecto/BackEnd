using System.Security.Claims;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Infrastructure.Configuration;
using BuildingFex.Api.Communications.Infrastructure.Storage;
using BuildingFex.Api.ResidentCare.Application;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.Communications.Interfaces.Rest;

/// <summary>
/// Uploads de Import — solo admin.
/// Flujo: signed-url (valida límites) → PUT a Storage → confirm → indexado asíncrono opcional.
/// </summary>
[ApiController]
[Route("api/v1/import/uploads")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class ImportUploadsController(
    AppDbContext db,
    IUserRepository users,
    IFirebaseStorageService storage,
    IAiContextIndexingQueue queue,
    IAiContextIndexer contextIndexer,
    IOptions<ImportSettings> options) : ControllerBase
{
    /// <summary>Filtros opcionales: documentStatus (PUBLISHED|SUPERSEDED|ARCHIVED), documentType, current=true.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? documentStatus,
        [FromQuery] string? documentType,
        [FromQuery] bool? current,
        CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var query = db.Set<ImportUpload>()
            .Where(u => u.OwnerAdminId == admin && u.UploadStatus == ImportUploadStatus.Confirmed);
        var status = current == true ? OfficialDocumentStatus.Published : OfficialDocumentStatus.Normalize(documentStatus);
        if (status is not null)
            query = query.Where(u => u.DocumentStatus == status);
        var type = OfficialDocumentType.Normalize(documentType);
        if (type is not null)
            query = query.Where(u => u.DocumentType == type);

        var items = await query
            .OrderByDescending(u => u.UploadedAt)
            .ToListAsync(ct);

        return Ok(items.Select(Map));
    }

    /// <summary>
    /// Reserva fila pending_upload + URL firmada de subida (PUT).
    /// Valida size/MIME/cuota ANTES de tocar el bucket.
    /// </summary>
    [HttpPost("signed-url")]
    public async Task<IActionResult> CreateSignedUploadUrl(
        [FromBody] SignedUploadRequest body,
        CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        if (!storage.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "STORAGE_NOT_CONFIGURED",
                message = "Firebase Storage credentials no configuradas en el servidor.",
            });
        }

        var settings = options.Value;
        var validation = ValidateLimits(admin, body.Size, body.MimeType, settings, ct);
        var error = await validation;
        if (error is not null) return error;

        var mime = NormalizeMime(body.MimeType);
        var id = string.IsNullOrWhiteSpace(body.Id)
            ? $"import-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : body.Id.Trim();
        var fileName = string.IsNullOrWhiteSpace(body.FileName) ? "archivo" : body.FileName.Trim();
        var safeName = SanitizeFileName(fileName);
        var storagePath = $"owners/{admin}/import/{id}/{safeName}";
        var folderId = string.IsNullOrWhiteSpace(body.FolderId) ? null : body.FolderId.Trim();
        if (!string.IsNullOrWhiteSpace(body.DocumentType) && OfficialDocumentType.Normalize(body.DocumentType) is null)
            return InvalidDocumentType();

        var upload = ImportUpload.CreatePending(
            id, admin, fileName, mime, body.Size, storagePath, folderId, body.DocumentType);

        db.Set<ImportUpload>().Add(upload);
        await db.SaveChangesAsync(ct);

        var ttl = TimeSpan.FromMinutes(Math.Clamp(settings.SignedUrlTtlMinutes, 5, 15));
        string uploadUrl;
        try
        {
            uploadUrl = await storage.CreateUploadUrlAsync(storagePath, mime, body.Size, ttl, ct);
        }
        catch (Exception ex)
        {
            db.Set<ImportUpload>().Remove(upload);
            await db.SaveChangesAsync(ct);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "SIGNED_URL_FAILED",
                message = ex.Message,
            });
        }

        return Ok(new
        {
            id = upload.ExternalId,
            storagePath = upload.StoragePath,
            uploadUrl,
            expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToString("o"),
            contentType = mime,
            contentLength = body.Size,
            method = "PUT",
        });
    }

    /// <summary>
    /// Confirma el PUT. Verifica metadata real en Storage vs lo declarado; si no cuadra, borra y rechaza.
    /// </summary>
    [HttpPost("{id}/confirm")]
    public async Task<IActionResult> Confirm(string id, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var upload = await FindUploadAsync(id, admin, ct);
        if (upload is null) return NotFound();

        if (upload.UploadStatus == ImportUploadStatus.Confirmed)
            return Ok(Map(upload));

        if (!storage.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "STORAGE_NOT_CONFIGURED",
                message = "Firebase Storage credentials no configuradas.",
            });
        }

        var meta = await storage.GetMetadataAsync(upload.StoragePath, ct);
        if (meta is null)
        {
            return BadRequest(new
            {
                code = "OBJECT_MISSING",
                message = "No hay objeto en Storage para este upload. Sube con la URL firmada y vuelve a confirmar.",
            });
        }

        var declaredMime = NormalizeMime(upload.MimeType);
        var actualMime = NormalizeMime(meta.ContentType);
        var sizeOk = meta.Size == upload.Size;
        var mimeOk = string.Equals(declaredMime, actualMime, StringComparison.OrdinalIgnoreCase);

        if (!sizeOk || !mimeOk)
        {
            try { await storage.DeleteAsync(upload.StoragePath, ct); }
            catch { /* best-effort */ }

            db.Set<ImportUpload>().Remove(upload);
            await db.SaveChangesAsync(ct);

            return BadRequest(new
            {
                code = "UPLOAD_MISMATCH",
                message = "El archivo en Storage no coincide con lo declarado (size/MIME).",
                declared = new { size = upload.Size, mimeType = declaredMime },
                actual = new { size = meta.Size, mimeType = actualMime },
            });
        }

        var needsIndex = await IsContextFolderAsync(admin, upload.FolderId, ct);
        upload.Confirm(needsIndex);
        await db.SaveChangesAsync(ct);

        if (needsIndex)
            await queue.EnqueueAsync(upload.ExternalId, ct);

        return Ok(Map(upload));
    }

    /// <summary>URL firmada de descarga de corta duración (no persiste en MySQL).</summary>
    [HttpGet("{id}/download-url")]
    public async Task<IActionResult> GetDownloadUrl(string id, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var upload = await FindUploadAsync(id, admin, ct);
        if (upload is null || upload.UploadStatus != ImportUploadStatus.Confirmed)
            return NotFound();

        if (string.IsNullOrWhiteSpace(upload.StoragePath))
        {
            return BadRequest(new
            {
                code = "NO_STORAGE_PATH",
                message = "Archivo legacy sin storage_path; no se puede firmar descarga.",
            });
        }

        if (!storage.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "STORAGE_NOT_CONFIGURED",
                message = "Firebase Storage credentials no configuradas.",
            });
        }

        var ttl = TimeSpan.FromMinutes(Math.Clamp(options.Value.SignedUrlTtlMinutes, 5, 15));
        var url = await storage.CreateDownloadUrlAsync(upload.StoragePath, ttl, ct);
        return Ok(new
        {
            id = upload.ExternalId,
            fileName = upload.FileName,
            downloadUrl = url,
            expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToString("o"),
        });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] PatchUploadBody body, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var upload = await FindUploadAsync(id, admin, ct);
        if (upload is null) return NotFound();
        if (upload.UploadStatus != ImportUploadStatus.Confirmed)
            return BadRequest(new { code = "NOT_CONFIRMED", message = "Confirma la subida primero." });

        if (body.DocumentType is not null)
        {
            if (OfficialDocumentType.Normalize(body.DocumentType) is null) return InvalidDocumentType();
            upload.SetDocumentType(body.DocumentType);
        }

        var previousFolderId = upload.FolderId;
        upload.Patch(body.FileName, body.FolderId);
        var folderChanged = !string.Equals(previousFolderId, upload.FolderId, StringComparison.Ordinal);
        var shouldEnqueue = false;

        if (folderChanged)
        {
            var inContext = await IsContextFolderAsync(admin, upload.FolderId, ct);
            if (inContext && upload.IsCurrent)
            {
                upload.SetIndexingStatus(ImportIndexingStatus.Pending);
                shouldEnqueue = true;
            }
            else
            {
                upload.SetIndexingStatus(ImportIndexingStatus.None);
            }
        }

        await db.SaveChangesAsync(ct);

        if (folderChanged)
        {
            if (shouldEnqueue)
                await queue.EnqueueAsync(upload.ExternalId, ct);
            else
                await contextIndexer.RemoveUploadAsync(upload.ExternalId, ct);
        }

        return Ok(Map(upload));
    }

    /// <summary>
    /// PublishDocument (Tablas 77-78): deja el documento vigente. Con supersedesId, la versión anterior
    /// queda SUPERSEDED (versión +1, mismo tipo si no se indica otro).
    /// </summary>
    [HttpPost("{id}/publish")]
    public async Task<IActionResult> Publish(string id, [FromBody] PublishDocumentBody? body, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var upload = await FindUploadAsync(id, admin, ct);
        if (upload is null) return NotFound();
        if (upload.UploadStatus != ImportUploadStatus.Confirmed)
            return BadRequest(new { code = "NOT_CONFIRMED", message = "Confirma la subida primero." });
        if (body?.DocumentType is not null && OfficialDocumentType.Normalize(body.DocumentType) is null)
            return InvalidDocumentType();

        ImportUpload? previous = null;
        if (!string.IsNullOrWhiteSpace(body?.SupersedesId))
        {
            previous = await FindUploadAsync(body.SupersedesId, admin, ct);
            if (previous is null)
                return NotFound(new { code = "SUPERSEDED_DOCUMENT_NOT_FOUND", message = "Documento a reemplazar no encontrado." });
            if (previous.ExternalId == upload.ExternalId)
                return BadRequest(new { code = "SUPERSEDES_SELF", message = "Un documento no puede reemplazarse a sí mismo." });
            if (previous.UploadStatus != ImportUploadStatus.Confirmed)
                return BadRequest(new { code = "NOT_CONFIRMED", message = "El documento a reemplazar no está confirmado." });
        }

        var wasCurrent = upload.IsCurrent;
        upload.Publish(previous, body?.DocumentType);
        if (previous is not null)
        {
            previous.Supersede();
            previous.SetIndexingStatus(ImportIndexingStatus.None);
        }

        var shouldEnqueue = false;
        if (!wasCurrent && await IsContextFolderAsync(admin, upload.FolderId, ct))
        {
            upload.SetIndexingStatus(ImportIndexingStatus.Pending);
            shouldEnqueue = true;
        }

        await db.SaveChangesAsync(ct);

        if (previous is not null)
            await contextIndexer.RemoveUploadAsync(previous.ExternalId, ct);
        if (shouldEnqueue)
            await queue.EnqueueAsync(upload.ExternalId, ct);

        return Ok(new
        {
            document = Map(upload),
            superseded = previous is null ? null : Map(previous),
        });
    }

    /// <summary>Archiva el documento: deja de ser vigente.</summary>
    [HttpPost("{id}/archive")]
    public async Task<IActionResult> Archive(string id, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var upload = await FindUploadAsync(id, admin, ct);
        if (upload is null) return NotFound();
        if (upload.UploadStatus != ImportUploadStatus.Confirmed)
            return BadRequest(new { code = "NOT_CONFIRMED", message = "Confirma la subida primero." });

        upload.Archive();
        upload.SetIndexingStatus(ImportIndexingStatus.None);
        await db.SaveChangesAsync(ct);
        await contextIndexer.RemoveUploadAsync(upload.ExternalId, ct);

        return Ok(Map(upload));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(ct);
        if (admin is null) return AdminFail();

        var upload = await FindUploadAsync(id, admin, ct);
        if (upload is null) return NotFound();

        await contextIndexer.RemoveUploadAsync(upload.ExternalId, ct);

        if (!string.IsNullOrWhiteSpace(upload.StoragePath) && storage.IsConfigured)
        {
            try { await storage.DeleteAsync(upload.StoragePath, ct); }
            catch { /* best-effort */ }
        }

        db.Set<ImportUpload>().Remove(upload);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<IActionResult?> ValidateLimits(
        string admin,
        long size,
        string? mimeType,
        ImportSettings settings,
        CancellationToken ct)
    {
        if (size <= 0 || size > settings.MaxBytes)
        {
            return BadRequest(new
            {
                code = "FILE_TOO_LARGE",
                message = $"Tamaño máximo {settings.MaxBytes} bytes.",
                maxBytes = settings.MaxBytes,
            });
        }

        var mime = NormalizeMime(mimeType);
        if (!settings.AllowedMimeTypes.Any(m => string.Equals(m, mime, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(new
            {
                code = "MIME_NOT_ALLOWED",
                message = "Tipo de archivo no permitido.",
                allowed = settings.AllowedMimeTypes,
            });
        }

        var count = await db.Set<ImportUpload>().CountAsync(u => u.OwnerAdminId == admin, ct);
        if (count >= settings.MaxFilesPerOwner)
        {
            return BadRequest(new
            {
                code = "QUOTA_EXCEEDED",
                message = $"Máximo {settings.MaxFilesPerOwner} archivos por condominio.",
            });
        }

        return null;
    }

    private IActionResult InvalidDocumentType() =>
        BadRequest(new
        {
            code = "INVALID_DOCUMENT_TYPE",
            message = "Tipo de documento no válido.",
            allowed = OfficialDocumentType.All,
        });

    private IActionResult AdminFail() =>
        Unauthorized(new { code = "ADMIN_REQUIRED", message = "Import solo para administradores." });

    /// <summary>ownerAdminId del condominio activo, si quien llama lo gestiona (dueño, administrador asignado o junta).</summary>
    private async Task<string?> RequireAdminAsync(CancellationToken ct)
    {
        var caller = await HttpContext.RequestServices
            .GetRequiredService<BuildingFex.Api.Iam.Application.Internal.Tenancy.ICallerContextAccessor>()
            .GetAsync(ct);
        return caller is not null && caller.IsAdmin ? caller.TenantExternalId : null;
    }

    private async Task<bool> IsContextFolderAsync(string admin, string? folderId, CancellationToken ct)
    {
        var clean = (folderId ?? string.Empty).Trim();
        if (clean.Length == 0) return false;
        return await db.Set<ImportFolder>()
            .AsNoTracking()
            .AnyAsync(
                f => f.OwnerAdminId == admin
                     && f.IsContext
                     && (f.ExternalId == clean || f.Id.ToString() == clean),
                ct);
    }

    private async Task<ImportUpload?> FindUploadAsync(string id, string owner, CancellationToken ct)
    {
        var clean = (id ?? string.Empty).Trim();
        return await db.Set<ImportUpload>()
            .FirstOrDefaultAsync(
                u => u.OwnerAdminId == owner && (u.ExternalId == clean || u.Id.ToString() == clean),
                ct);
    }

    private static string NormalizeMime(string? mime) =>
        string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime.Trim().ToLowerInvariant();

    private static string SanitizeFileName(string name)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"[^\w.\-()+ ]+", "_");
        return cleaned.Length > 180 ? cleaned[..180] : cleaned;
    }

    private static object Map(ImportUpload u) => new
    {
        id = u.ExternalId,
        ownerAdminId = u.OwnerAdminId,
        fileName = u.FileName,
        mimeType = u.MimeType,
        size = u.Size,
        storagePath = u.StoragePath,
        folderId = u.FolderId,
        uploadStatus = u.UploadStatus,
        uploadedAt = u.UploadedAt.ToString("o"),
        documentType = u.DocumentType,
        version = u.Version,
        publishedAt = u.PublishedAt?.ToString("o"),
        documentStatus = u.DocumentStatus,
        supersedesId = u.SupersedesId,
        isCurrent = u.IsCurrent,
        indexingStatus = u.IndexingStatus,
        indexingError = u.IndexingError,
    };
}

public class SignedUploadRequest
{
    public string? Id { get; set; }
    public string? FileName { get; set; }
    public string? MimeType { get; set; }
    public long Size { get; set; }
    public string? FolderId { get; set; }
    /// <summary>REGULATION | MINUTES | OTHER (opcional).</summary>
    public string? DocumentType { get; set; }
}

public class PatchUploadBody
{
    public string? FileName { get; set; }
    public string? FolderId { get; set; }
    public string? DocumentType { get; set; }
}

public class PublishDocumentBody
{
    /// <summary>Documento vigente que esta versión reemplaza (queda SUPERSEDED).</summary>
    public string? SupersedesId { get; set; }
    public string? DocumentType { get; set; }
}
