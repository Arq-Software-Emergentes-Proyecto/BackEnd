namespace BuildingFex.Api.Communications.Domain.Model.Aggregates;

public static class ImportUploadStatus
{
    /// <summary>Fila reservada; el binario aún no se confirmó en Storage.</summary>
    public const string PendingUpload = "pending_upload";
    /// <summary>Binario confirmado; visible en listados.</summary>
    public const string Confirmed = "confirmed";
}

public static class ImportIndexingStatus
{
    public const string None = "none";
    public const string Pending = "pending";
    public const string Indexing = "indexing";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>DocumentType (Tabla 76): tipo de documento oficial.</summary>
public static class OfficialDocumentType
{
    public const string Regulation = "REGULATION";
    public const string Minutes = "MINUTES";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All = [Regulation, Minutes, Other];

    public static string? Normalize(string? value)
    {
        var clean = (value ?? string.Empty).Trim().ToUpperInvariant();
        return All.Contains(clean) ? clean : null;
    }
}

/// <summary>
/// DocumentStatus (Tabla 76). Solo los PUBLISHED son "documentos vigentes" (RF033): el asistente IA
/// responde únicamente con ellos; SUPERSEDED (reemplazado por una versión nueva) y ARCHIVED dejan de usarse.
/// </summary>
public static class OfficialDocumentStatus
{
    public const string PendingUpload = "PENDING_UPLOAD";
    public const string Published = "PUBLISHED";
    public const string Superseded = "SUPERSEDED";
    public const string Archived = "ARCHIVED";

    public static readonly IReadOnlyList<string> All = [PendingUpload, Published, Superseded, Archived];

    public static string? Normalize(string? value)
    {
        var clean = (value ?? string.Empty).Trim().ToUpperInvariant();
        return All.Contains(clean) ? clean : null;
    }
}

/// <summary>
/// OfficialDocument (Tabla 76) sobre la tabla import_uploads: archivo + tipo, versión y vigencia.
/// Metadata de archivo. El binario vive en Firebase Storage (solo StoragePath en MySQL).
/// DownloadUrl legacy se vacía; las URLs de descarga se firman on-demand.
/// </summary>
public class ImportUpload
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string OwnerAdminId { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string MimeType { get; private set; } = string.Empty;
    public long Size { get; private set; }
    /// <summary>Ruta en Firebase Storage, p.ej. owners/{owner}/import/{id}/{name}</summary>
    public string StoragePath { get; private set; } = string.Empty;
    /// <summary>Legacy. No se escribe en el camino nuevo.</summary>
    public string DownloadUrl { get; private set; } = string.Empty;
    /// <summary>Legacy dataUrl base64.</summary>
    public string DataUrl { get; private set; } = string.Empty;
    public string? FolderId { get; private set; }
    public string UploadStatus { get; private set; } = ImportUploadStatus.PendingUpload;
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>REGULATION | MINUTES | OTHER</summary>
    public string DocumentType { get; private set; } = OfficialDocumentType.Other;

    public int Version { get; private set; } = 1;
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>PENDING_UPLOAD | PUBLISHED | SUPERSEDED | ARCHIVED</summary>
    public string DocumentStatus { get; private set; } = OfficialDocumentStatus.Published;

    /// <summary>Documento anterior al que esta versión dejó sin vigencia (external id).</summary>
    public string? SupersedesId { get; private set; }

    /// <summary>Estado de indexación RAG: none | pending | indexing | done | failed.</summary>
    public string IndexingStatus { get; private set; } = ImportIndexingStatus.None;
    public string? IndexingError { get; private set; }

    /// <summary>Vigente: confirmado y publicado.</summary>
    public bool IsCurrent =>
        UploadStatus == ImportUploadStatus.Confirmed && DocumentStatus == OfficialDocumentStatus.Published;

    private ImportUpload() { }

    public static ImportUpload CreatePending(
        string externalId,
        string ownerAdminId,
        string fileName,
        string mimeType,
        long size,
        string storagePath,
        string? folderId = null,
        string? documentType = null)
    {
        return new ImportUpload
        {
            ExternalId = externalId.Trim(),
            OwnerAdminId = ownerAdminId.Trim(),
            FileName = fileName.Trim(),
            MimeType = mimeType.Trim(),
            Size = size,
            StoragePath = (storagePath ?? string.Empty).Trim(),
            DownloadUrl = string.Empty,
            DataUrl = string.Empty,
            FolderId = string.IsNullOrWhiteSpace(folderId) ? null : folderId.Trim(),
            UploadStatus = ImportUploadStatus.PendingUpload,
            UploadedAt = DateTimeOffset.UtcNow,
            DocumentType = OfficialDocumentType.Normalize(documentType) ?? OfficialDocumentType.Other,
            DocumentStatus = OfficialDocumentStatus.PendingUpload,
            Version = 1,
            IndexingStatus = ImportIndexingStatus.None,
        };
    }

    /// <summary>confirmUpload(): el binario está en Storage. Se publica de inmediato (comportamiento histórico).</summary>
    public void Confirm(bool needsIndexing = false)
    {
        UploadStatus = ImportUploadStatus.Confirmed;
        if (DocumentStatus == OfficialDocumentStatus.PendingUpload)
        {
            DocumentStatus = OfficialDocumentStatus.Published;
            PublishedAt = DateTimeOffset.UtcNow;
        }
        DownloadUrl = string.Empty;
        IndexingStatus = needsIndexing ? ImportIndexingStatus.Pending : ImportIndexingStatus.None;
        IndexingError = null;
    }

    public void SetIndexingStatus(string status, string? error = null)
    {
        IndexingStatus = string.IsNullOrWhiteSpace(status) ? ImportIndexingStatus.None : status.Trim();
        var clean = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        IndexingError = clean is not null && clean.Length > 1024 ? clean[..1024] : clean;
    }

    public void Patch(string? fileName, string? folderId)
    {
        if (fileName is not null)
            FileName = fileName.Trim();
        if (folderId is not null)
            FolderId = string.IsNullOrWhiteSpace(folderId) ? null : folderId.Trim();
    }

    public void SetDocumentType(string documentType)
    {
        DocumentType = OfficialDocumentType.Normalize(documentType)
                       ?? throw new ArgumentException("Tipo de documento no válido.", nameof(documentType));
    }

    /// <summary>
    /// publish(): el documento pasa a vigente. Si reemplaza a otro, toma su tipo (si no se indicó) y la
    /// versión siguiente; el anterior debe marcarse con <see cref="Supersede"/>.
    /// </summary>
    public void Publish(ImportUpload? supersedes = null, string? documentType = null)
    {
        if (UploadStatus != ImportUploadStatus.Confirmed)
            throw new InvalidOperationException("Confirma la subida antes de publicar.");

        if (documentType is not null)
            SetDocumentType(documentType);
        else if (supersedes is not null)
            DocumentType = supersedes.DocumentType;

        if (supersedes is not null)
        {
            Version = Math.Max(Version, supersedes.Version + 1);
            SupersedesId = supersedes.ExternalId;
        }

        if (DocumentStatus != OfficialDocumentStatus.Published || PublishedAt is null)
            PublishedAt = DateTimeOffset.UtcNow;
        DocumentStatus = OfficialDocumentStatus.Published;
    }

    /// <summary>supersede(): una versión nueva lo dejó sin vigencia.</summary>
    public void Supersede()
    {
        if (DocumentStatus == OfficialDocumentStatus.PendingUpload)
            throw new InvalidOperationException("Un documento sin confirmar no puede reemplazarse.");
        DocumentStatus = OfficialDocumentStatus.Superseded;
    }

    public void Archive()
    {
        if (DocumentStatus == OfficialDocumentStatus.PendingUpload)
            throw new InvalidOperationException("Un documento sin confirmar no puede archivarse.");
        DocumentStatus = OfficialDocumentStatus.Archived;
    }
}
