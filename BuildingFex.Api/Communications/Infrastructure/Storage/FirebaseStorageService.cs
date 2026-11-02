using System.Text;
using BuildingFex.Api.Communications.Infrastructure.Configuration;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.Communications.Infrastructure.Storage;

public record StorageObjectMetadata(long Size, string ContentType);

public interface IFirebaseStorageService
{
    bool IsConfigured { get; }
    Task<string> CreateUploadUrlAsync(
        string storagePath,
        string contentType,
        long contentLength,
        TimeSpan ttl,
        CancellationToken ct = default);
    Task<string> CreateDownloadUrlAsync(string storagePath, TimeSpan ttl, CancellationToken ct = default);
    Task<StorageObjectMetadata?> GetMetadataAsync(string storagePath, CancellationToken ct = default);
    Task<byte[]> DownloadBytesAsync(string storagePath, CancellationToken ct = default);
    Task DeleteAsync(string storagePath, CancellationToken ct = default);
}

/// <summary>
/// Acceso server-side a Firebase Storage (GCS). Firma URLs V4 y descarga/borra por storage_path.
/// </summary>
public class FirebaseStorageService : IFirebaseStorageService
{
    private readonly FirebaseStorageSettings _settings;
    private readonly object _gate = new();
    private StorageClient? _client;
    private UrlSigner? _signer;
    private bool _initAttempted;
    private Exception? _initError;

    public FirebaseStorageService(IOptions<FirebaseStorageSettings> options)
    {
        _settings = options.Value;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.Bucket) && HasCredentialSource();

    public async Task<string> CreateUploadUrlAsync(
        string storagePath,
        string contentType,
        long contentLength,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        var path = RequirePath(storagePath);
        var mime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();
        if (contentLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentLength), "contentLength must be > 0");

        var signer = GetSigner();

        // Content-Type + Content-Length van en la firma: GCS rechaza PUT si no coinciden.
        var template = UrlSigner.RequestTemplate
            .FromBucket(_settings.Bucket)
            .WithObjectName(path)
            .WithHttpMethod(HttpMethod.Put)
            .WithContentHeaders(new Dictionary<string, IEnumerable<string>>
            {
                ["Content-Type"] = [mime],
                ["Content-Length"] = [contentLength.ToString()],
            });

        var options = UrlSigner.Options.FromDuration(ClampTtl(ttl));
        return await signer.SignAsync(template, options, cancellationToken: ct);
    }

    public async Task<string> CreateDownloadUrlAsync(
        string storagePath,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        var path = RequirePath(storagePath);
        var signer = GetSigner();
        var template = UrlSigner.RequestTemplate
            .FromBucket(_settings.Bucket)
            .WithObjectName(path)
            .WithHttpMethod(HttpMethod.Get);
        var options = UrlSigner.Options.FromDuration(ClampTtl(ttl));
        return await signer.SignAsync(template, options, cancellationToken: ct);
    }

    public async Task<StorageObjectMetadata?> GetMetadataAsync(
        string storagePath,
        CancellationToken ct = default)
    {
        var path = RequirePath(storagePath);
        try
        {
            var client = GetClient();
            var obj = await client.GetObjectAsync(_settings.Bucket, path, cancellationToken: ct);
            var size = obj.Size.HasValue ? (long)obj.Size.Value : 0L;
            var contentType = obj.ContentType ?? string.Empty;
            return new StorageObjectMetadata(size, contentType);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
        {
            return null;
        }
    }

    public async Task<byte[]> DownloadBytesAsync(string storagePath, CancellationToken ct = default)
    {
        var path = RequirePath(storagePath);
        var client = GetClient();
        await using var ms = new MemoryStream();
        await client.DownloadObjectAsync(_settings.Bucket, path, ms, cancellationToken: ct);
        return ms.ToArray();
    }

    public async Task DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        var path = (storagePath ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(path) || !IsConfigured) return;

        try
        {
            var client = GetClient();
            await client.DeleteObjectAsync(_settings.Bucket, path, cancellationToken: ct);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
        {
            // already gone
        }
    }

    private StorageClient GetClient()
    {
        EnsureInit();
        return _client!;
    }

    private UrlSigner GetSigner()
    {
        EnsureInit();
        return _signer!;
    }

    private void EnsureInit()
    {
        if (_client is not null && _signer is not null) return;
        lock (_gate)
        {
            if (_client is not null && _signer is not null) return;
            if (_initAttempted && _initError is not null) throw _initError;
            _initAttempted = true;
            try
            {
                var credential = LoadCredential()
                    ?? throw new InvalidOperationException(
                        "Firebase Storage credentials missing. Set FirebaseStorage:CredentialsJson " +
                        "(service account JSON or base64) or CredentialsPath / GOOGLE_APPLICATION_CREDENTIALS.");

                if (string.IsNullOrWhiteSpace(_settings.Bucket))
                    throw new InvalidOperationException("FirebaseStorage:Bucket is required.");

                _client = StorageClient.Create(credential);
                _signer = UrlSigner.FromCredential(credential);
            }
            catch (Exception ex)
            {
                _initError = ex;
                throw;
            }
        }
    }

    private bool HasCredentialSource()
    {
        if (!string.IsNullOrWhiteSpace(_settings.CredentialsJson)) return true;
        if (!string.IsNullOrWhiteSpace(_settings.CredentialsPath) && File.Exists(_settings.CredentialsPath))
            return true;
        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        return !string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath);
    }

    private GoogleCredential? LoadCredential()
    {
        var json = (_settings.CredentialsJson ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(json))
        {
            if (!json.StartsWith('{'))
            {
                try
                {
                    json = Encoding.UTF8.GetString(Convert.FromBase64String(json));
                }
                catch
                {
                    // raw JSON
                }
            }

            return GoogleCredential.FromJson(json);
        }

        var path = (_settings.CredentialsPath ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            return GoogleCredential.FromFile(path);

        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return GoogleCredential.FromFile(envPath);

        return null;
    }

    private static string RequirePath(string storagePath)
    {
        var path = (storagePath ?? string.Empty).Trim().TrimStart('/');
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("storage_path is required.", nameof(storagePath));
        return path;
    }

    private static TimeSpan ClampTtl(TimeSpan ttl)
    {
        if (ttl < TimeSpan.FromMinutes(1)) return TimeSpan.FromMinutes(1);
        if (ttl > TimeSpan.FromMinutes(60)) return TimeSpan.FromMinutes(60);
        return ttl;
    }
}
