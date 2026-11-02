using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt;

/// <summary>
/// Un solo esquema: Firebase idToken (custom claims role + ownerAdminId).
/// Sin App JWT — DualJwt eliminado.
/// </summary>
public static class FirebaseAuthenticationExtensions
{
    public const string FirebaseScheme = "Firebase";

    public static AuthenticationBuilder AddBuildingFexAuthentication(
        this IServiceCollection services,
        TokenSettings tokenSettings,
        FirebaseAuthSettings firebaseSettings)
    {
        // tokenSettings retained for legacy TokenService (password bootstrap only); auth is Firebase-only.
        _ = tokenSettings;

        var builder = services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = FirebaseScheme;
                options.DefaultChallengeScheme = FirebaseScheme;
            });

        if (firebaseSettings.IsConfigured)
        {
            var projectId = firebaseSettings.ProjectId.Trim();
            builder.AddJwtBearer(FirebaseScheme, options =>
            {
                options.Authority = $"https://securetoken.google.com/{projectId}";
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://securetoken.google.com/{projectId}",
                    ValidateAudience = true,
                    ValidAudience = projectId,
                    ValidateLifetime = true,
                    NameClaimType = "user_id",
                    RoleClaimType = "role",
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = SignalRAccessTokenFromQuery,
                    OnTokenValidated = EnrichPrincipalAsync,
                };
            });
        }
        else
        {
            // Fail closed if Firebase not configured
            builder.AddJwtBearer(FirebaseScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        System.Text.Encoding.UTF8.GetBytes(
                            string.IsNullOrWhiteSpace(tokenSettings.Secret)
                                ? "firebase-not-configured-placeholder-key-32b"
                                : tokenSettings.Secret)),
                    ValidIssuer = "firebase-not-configured",
                    ValidAudience = "firebase-not-configured",
                };
            });
        }

        return builder;
    }

    /// <summary>
    /// La fila users es la fuente de verdad de rol y tenant: los custom claims de Firebase
    /// pueden quedar obsoletos hasta 1 h (residente eliminado, etc.). Si el uid no está
    /// aprovisionado se eliminan los claims de rol (fail-closed) y solo quedan endpoints
    /// que exigen autenticación sin rol (p.ej. POST /api/v1/profiles del primer admin).
    /// </summary>
    private static async Task EnrichPrincipalAsync(TokenValidatedContext context)
    {
        var identity = context.Principal?.Identity as ClaimsIdentity;
        if (identity is null) return;

        var uid = context.Principal?.FindFirst("user_id")?.Value
                  ?? context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrWhiteSpace(uid)) return;

        if (identity.FindFirst(ClaimTypes.NameIdentifier) is null)
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, uid));

        Domain.Model.Aggregates.User? user = null;
        try
        {
            var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
            user = await CallerContextAccessor.FindUserByFirebaseUidAsync(
                users, uid, context.HttpContext.RequestAborted);
        }
        catch
        {
            user = null;
        }

        foreach (var stale in identity.Claims
                     .Where(c => c.Type is "role" or ClaimTypes.Role or "ownerAdminId")
                     .ToList())
        {
            identity.TryRemoveClaim(stale);
        }

        if (user is null) return;

        context.HttpContext.Items[CallerContextAccessor.HttpItemKey] = user;
        identity.AddClaim(new Claim("role", user.Role));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));

        var ownerExternalId = user.Role == "admin" ? user.ExternalId : user.OwnerAdmin?.ExternalId;
        if (!string.IsNullOrWhiteSpace(ownerExternalId))
            identity.AddClaim(new Claim("ownerAdminId", ownerExternalId));
    }

    private static Task SignalRAccessTokenFromQuery(MessageReceivedContext context)
    {
        var accessToken = context.Request.Query["access_token"].ToString();
        var path = context.HttpContext.Request.Path;
        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/support"))
            context.Token = accessToken;
        return Task.CompletedTask;
    }
}
