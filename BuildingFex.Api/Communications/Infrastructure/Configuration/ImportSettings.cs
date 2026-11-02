namespace BuildingFex.Api.Communications.Infrastructure.Configuration;

public class ImportSettings
{
    public const string SectionName = "Import";

    /// <summary>Tamaño máximo por archivo (bytes). Default 5 MB.</summary>
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Máximo de archivos por condominio (owner admin), incluye pending.</summary>
    public int MaxFilesPerOwner { get; set; } = 100;

    /// <summary>TTL de URLs firmadas (subida y descarga), minutos.</summary>
    public int SignedUrlTtlMinutes { get; set; } = 10;

    /// <summary>Horas tras las cuales un pending_upload sin confirm se limpia.</summary>
    public int OrphanTtlHours { get; set; } = 2;

    /// <summary>MIME permitidos (whitelist).</summary>
    public string[] AllowedMimeTypes { get; set; } =
    [
        "text/plain",
        "text/markdown",
        "text/csv",
        "application/json",
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/webp",
    ];
}

public class FirebaseStorageSettings
{
    public const string SectionName = "FirebaseStorage";

    /// <summary>Nombre del bucket GCS (p.ej. project.appspot.com o project.firebasestorage.app).</summary>
    public string Bucket { get; set; } = "emergentes-36306.firebasestorage.app";

    /// <summary>JSON de service account (texto o base64). Preferido en Railway.</summary>
    public string CredentialsJson { get; set; } = string.Empty;

    /// <summary>Ruta a archivo de credenciales (dev local).</summary>
    public string CredentialsPath { get; set; } = string.Empty;
}
