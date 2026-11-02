using System.IdentityModel.Tokens.Jwt;
using BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt;

/// <param name="SignInProvider">Claim firebase.sign_in_provider: "google.com", "password", …</param>
public record FirebaseIdentity(string Uid, string Email, bool EmailVerified = false, string? SignInProvider = null)
{
    public bool IsPasswordProvider =>
        string.Equals(SignInProvider, "password", StringComparison.OrdinalIgnoreCase);
}

public interface IFirebaseIdTokenVerifier
{
    Task<FirebaseIdentity?> VerifyAsync(string? idToken, CancellationToken ct = default);
}

/// <summary>
/// Valida Firebase ID tokens (issuer securetoken.google.com) vía OIDC JWKS.
/// Usado en endpoints anónimos (sign-in-google / complete-google) donde no hay middleware Bearer.
/// </summary>
public class FirebaseIdTokenVerifier(
    IOptions<FirebaseAuthSettings> options,
    ILogger<FirebaseIdTokenVerifier> logger) : IFirebaseIdTokenVerifier
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConfigurationManager<OpenIdConnectConfiguration>? _configManager;

    public async Task<FirebaseIdentity?> VerifyAsync(string? idToken, CancellationToken ct = default)
    {
        var token = (idToken ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(token))
            return null;

        var projectId = options.Value.ProjectId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(projectId))
        {
            logger.LogWarning("Firebase ProjectId not configured; cannot verify idToken");
            return null;
        }

        try
        {
            var configManager = await GetConfigManagerAsync(projectId, ct);
            var oidc = await configManager.GetConfigurationAsync(ct);

            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = $"https://securetoken.google.com/{projectId}",
                ValidateAudience = true,
                ValidAudience = projectId,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidc.SigningKeys,
                NameClaimType = "user_id",
                ClockSkew = TimeSpan.FromMinutes(1),
            };

            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, parameters, out _);

            var uid = principal.FindFirst("user_id")?.Value
                      ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                      ?? string.Empty;
            var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
                        ?? principal.FindFirst("email")?.Value
                        ?? string.Empty;

            if (string.IsNullOrWhiteSpace(uid))
                return null;

            var emailVerified = string.Equals(
                principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);

            string? provider = null;
            var firebaseClaim = principal.FindFirst("firebase")?.Value;
            if (!string.IsNullOrWhiteSpace(firebaseClaim))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(firebaseClaim);
                    if (doc.RootElement.TryGetProperty("sign_in_provider", out var p)
                        && p.ValueKind == System.Text.Json.JsonValueKind.String)
                        provider = p.GetString();
                }
                catch (System.Text.Json.JsonException)
                {
                    provider = null;
                }
            }

            return new FirebaseIdentity(uid.Trim(), email.Trim().ToLowerInvariant(), emailVerified, provider);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Firebase idToken verification failed");
            return null;
        }
    }

    private async Task<ConfigurationManager<OpenIdConnectConfiguration>> GetConfigManagerAsync(
        string projectId,
        CancellationToken ct)
    {
        if (_configManager is not null)
            return _configManager;

        await _gate.WaitAsync(ct);
        try
        {
            if (_configManager is not null)
                return _configManager;

            var metadataAddress =
                $"https://securetoken.google.com/{projectId}/.well-known/openid-configuration";
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true });
            return _configManager;
        }
        finally
        {
            _gate.Release();
        }
    }
}
