using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace BuildingFex.Api.Communications.Infrastructure.Storage;

/// <summary>
/// Acceso server-side a Amazon S3: el navegador sube y descarga con URLs prefirmadas de corta duración;
/// el servidor solo firma, lee metadata, descarga (indexación) y borra. Bucket privado con credenciales del rol de la Lambda.
/// </summary>
public class S3StorageService(IAmazonS3 s3, string bucket) : IFirebaseStorageService
{
    public const string BucketVariable = "BF_FILES_BUCKET";

    private readonly string _bucket = (bucket ?? string.Empty).Trim();

    /// <summary>Nombre del bucket configurado en BF_FILES_BUCKET, o null si no existe o está vacío.</summary>
    public static string? ConfiguredBucket
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(BucketVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_bucket);

    public async Task<string> CreateUploadUrlAsync(
        string storagePath,
        string contentType,
        long contentLength,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        var path = RequirePath(storagePath);
        if (contentLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentLength), "contentLength must be > 0");

        var mime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();

        // ContentType va en la firma: S3 rechaza un PUT con otro tipo. El tamaño real se valida en GetMetadataAsync.
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = path,
            Verb = HttpVerb.PUT,
            ContentType = mime,
            Expires = DateTime.UtcNow.Add(ClampTtl(ttl)),
        };

        return await s3.GetPreSignedURLAsync(request);
    }

    public async Task<string> CreateDownloadUrlAsync(
        string storagePath,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        var path = RequirePath(storagePath);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = path,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ClampTtl(ttl)),
        };

        return await s3.GetPreSignedURLAsync(request);
    }

    public async Task<StorageObjectMetadata?> GetMetadataAsync(
        string storagePath,
        CancellationToken ct = default)
    {
        var path = RequirePath(storagePath);
        try
        {
            var response = await s3.GetObjectMetadataAsync(_bucket, path, ct);
            return new StorageObjectMetadata(response.ContentLength, response.Headers.ContentType ?? string.Empty);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]> DownloadBytesAsync(string storagePath, CancellationToken ct = default)
    {
        var path = RequirePath(storagePath);
        using var response = await s3.GetObjectAsync(_bucket, path, ct);
        await using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        var path = NormalizePath(storagePath);
        if (string.IsNullOrEmpty(path) || !IsConfigured) return;

        // S3 responde 204 aunque el objeto no exista.
        await s3.DeleteObjectAsync(_bucket, path, ct);
    }

    private static string NormalizePath(string? storagePath) =>
        (storagePath ?? string.Empty).Trim().TrimStart('/');

    private static string RequirePath(string storagePath)
    {
        var path = NormalizePath(storagePath);
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("storage_path is required.", nameof(storagePath));
        return path;
    }

    private static TimeSpan ClampTtl(TimeSpan ttl)
    {
        if (ttl < TimeSpan.FromMinutes(1)) return TimeSpan.FromMinutes(1);
        if (ttl > TimeSpan.FromMinutes(15)) return TimeSpan.FromMinutes(15);
        return ttl;
    }
}
