using Amazon.S3;
using Amazon.S3.Model;

namespace BuildingFex.Api.Communications.Infrastructure.Storage;

/// <summary>
/// Almacenamiento de archivos en Amazon S3 (despliegue en AWS). Mismo contrato que Firebase Storage:
/// el navegador sube y descarga con URLs prefirmadas de corta duración; el servidor solo firma, lee
/// metadata, descarga (indexación IA) y borra. El bucket es privado; las credenciales salen del rol
/// de la Lambda. Se activa con la variable BF_FILES_BUCKET (la define template.yaml).
/// </summary>
public class S3StorageService(IAmazonS3 s3, string bucket) : IFirebaseStorageService
{
    public const string BucketVariable = "BF_FILES_BUCKET";

    public static string? ConfiguredBucket =>
        Environment.GetEnvironmentVariable(BucketVariable) is { Length: > 0 } name ? name.Trim() : null;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(bucket);

    public Task<string> CreateUploadUrlAsync(
        string storagePath,
        string contentType,
        long contentLength,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        if (contentLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentLength), "contentLength must be > 0");

        // Content-Type va en la firma: S3 rechaza el PUT si el navegador manda otro. El tamaño real se
        // compara al confirmar (GetMetadataAsync) y el upload se borra si no coincide.
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = RequirePath(storagePath),
            Verb = HttpVerb.PUT,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
            Expires = DateTime.UtcNow.Add(ClampTtl(ttl)),
        };
        return s3.GetPreSignedURLAsync(request);
    }

    public Task<string> CreateDownloadUrlAsync(string storagePath, TimeSpan ttl, CancellationToken ct = default) =>
        s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = RequirePath(storagePath),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ClampTtl(ttl)),
        });

    public async Task<StorageObjectMetadata?> GetMetadataAsync(string storagePath, CancellationToken ct = default)
    {
        try
        {
            var meta = await s3.GetObjectMetadataAsync(bucket, RequirePath(storagePath), ct);
            return new StorageObjectMetadata(meta.ContentLength, meta.Headers.ContentType ?? string.Empty);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]> DownloadBytesAsync(string storagePath, CancellationToken ct = default)
    {
        using var response = await s3.GetObjectAsync(bucket, RequirePath(storagePath), ct);
        await using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        var path = (storagePath ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(path) || !IsConfigured) return;
        // S3 responde 204 aunque el objeto ya no exista.
        await s3.DeleteObjectAsync(bucket, path, ct);
    }

    private static string RequirePath(string storagePath)
    {
        var path = (storagePath ?? string.Empty).Trim().TrimStart('/');
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("storagePath required", nameof(storagePath));
        return path;
    }

    private static TimeSpan ClampTtl(TimeSpan ttl) =>
        ttl < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1)
        : ttl > TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(15)
        : ttl;
}
