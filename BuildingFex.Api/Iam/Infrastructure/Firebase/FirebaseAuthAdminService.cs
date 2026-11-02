using System.Text;
using BuildingFex.Api.Communications.Infrastructure.Configuration;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.Iam.Infrastructure.Firebase;

public interface IFirebaseAuthAdminService
{
    bool IsConfigured { get; }
    Task SetClaimsAsync(string uid, string role, string ownerAdminId, CancellationToken ct = default);
    Task ClearClaimsAndRevokeAsync(string uid, CancellationToken ct = default);
}

/// <summary>
/// Custom claims + revocación vía Firebase Admin SDK.
/// Un solo emisor (Firebase idToken) reemplaza el App JWT de residentes.
/// </summary>
public class FirebaseAuthAdminService : IFirebaseAuthAdminService
{
    private readonly FirebaseStorageSettings _creds;
    private readonly object _gate = new();
    private bool _initialized;
    private Exception? _initError;

    private readonly ILogger<FirebaseAuthAdminService> _logger;

    public FirebaseAuthAdminService(IOptions<FirebaseStorageSettings> creds, ILogger<FirebaseAuthAdminService> logger)
    {
        _creds = creds.Value;
        _logger = logger;
    }

    public bool IsConfigured => HasCredentialSource();

    public async Task SetClaimsAsync(
        string uid,
        string role,
        string ownerAdminId,
        CancellationToken ct = default)
    {
        var cleanUid = (uid ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(cleanUid))
            throw new ArgumentException("uid required", nameof(uid));
        if (!IsConfigured) return;

        // Los claims son solo un espejo: el servidor reconstruye rol y condominio desde la base en cada
        // request. Si Firebase falla, el login no debe fallar (ni consumir una invitación a medias).
        try
        {
            EnsureApp();
            var claims = new Dictionary<string, object>
            {
                ["role"] = (role ?? string.Empty).Trim(),
                ["ownerAdminId"] = (ownerAdminId ?? string.Empty).Trim(),
            };
            await FirebaseAuth.DefaultInstance.SetCustomUserClaimsAsync(cleanUid, claims);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not sync Firebase custom claims for {Uid}", cleanUid);
        }
    }

    public async Task ClearClaimsAndRevokeAsync(string uid, CancellationToken ct = default)
    {
        var cleanUid = (uid ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(cleanUid) || !IsConfigured) return;

        try
        {
            EnsureApp();
            await FirebaseAuth.DefaultInstance.SetCustomUserClaimsAsync(cleanUid, new Dictionary<string, object>());
            await FirebaseAuth.DefaultInstance.RevokeRefreshTokensAsync(cleanUid);
        }
        catch (FirebaseAuthException)
        {
            // usuario Firebase inexistente — ok
        }
    }

    private void EnsureApp()
    {
        if (FirebaseApp.DefaultInstance is not null) return;
        lock (_gate)
        {
            if (FirebaseApp.DefaultInstance is not null) return;
            if (_initialized && _initError is not null) throw _initError;
            _initialized = true;
            try
            {
                var credential = LoadCredential()
                    ?? throw new InvalidOperationException(
                        "Firebase Admin credentials missing (same as FirebaseStorage CredentialsJson).");
                FirebaseApp.Create(new AppOptions { Credential = credential });
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
        if (!string.IsNullOrWhiteSpace(_creds.CredentialsJson)) return true;
        if (!string.IsNullOrWhiteSpace(_creds.CredentialsPath) && File.Exists(_creds.CredentialsPath))
            return true;
        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        return !string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath);
    }

    private GoogleCredential? LoadCredential()
    {
        var json = (_creds.CredentialsJson ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(json))
        {
            if (!json.StartsWith('{'))
            {
                try { json = Encoding.UTF8.GetString(Convert.FromBase64String(json)); }
                catch { /* raw */ }
            }

            return GoogleCredential.FromJson(json);
        }

        var path = (_creds.CredentialsPath ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            return GoogleCredential.FromFile(path);

        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return GoogleCredential.FromFile(envPath);

        return null;
    }
}
