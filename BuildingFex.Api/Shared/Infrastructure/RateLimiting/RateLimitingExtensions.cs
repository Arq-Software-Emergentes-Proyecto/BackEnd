using System.Threading.RateLimiting;

namespace BuildingFex.Api.Shared.Infrastructure.RateLimiting;

public static class RateLimitPolicies
{
    /// <summary>Lookups anónimos de identidad (check-email, invite) — mitiga enumeración de cuentas.</summary>
    public const string AuthLookup = "auth-lookup";
}

public static class RateLimitingExtensions
{
    /// <summary>Largo máximo de la clave de partición (una IPv6 completa ocupa 45).</summary>
    public const int MaxPartitionKeyLength = 64;

    /// <param name="trustedProxyHops">
    /// Cantidad de proxies de confianza que AGREGAN una entrada al final de X-Forwarded-For (Railway: 1).
    /// Configurable con RateLimiting__TrustedProxyHops (p. ej. 2 si además hay un proxy de Vercel delante).
    /// </param>
    public static IServiceCollection AddBuildingFexRateLimiting(this IServiceCollection services, int trustedProxyHops = 1)
    {
        var hops = Math.Clamp(trustedProxyHops, 0, 10);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsync(
                    "{\"code\":\"TOO_MANY_REQUESTS\",\"message\":\"Demasiadas solicitudes. Intenta en un minuto.\"}",
                    ct);
            };

            options.AddPolicy(RateLimitPolicies.AuthLookup, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(http, hops),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
        });

        return services;
    }

    private const string RawForwardedForItem = "bf.raw-x-forwarded-for";
    private const string RawRemoteIpItem = "bf.raw-remote-ip";

    /// <summary>
    /// Guarda X-Forwarded-For y RemoteIpAddress tal como llegaron, ANTES de UseForwardedHeaders: ese middleware
    /// consume la entrada derecha cuando el proxy es "conocido" (loopback, o RemoteIpAddress nulo en TestServer)
    /// y deja en el header solo las entradas que controla el cliente.
    /// </summary>
    public static IApplicationBuilder UseClientIpCapture(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Items[RawForwardedForItem] = context.Request.Headers["X-Forwarded-For"].ToArray();
            context.Items[RawRemoteIpItem] = context.Connection.RemoteIpAddress?.ToString();
            return next(context);
        });

    /// <summary>
    /// IP del cliente para el cupo. Railway agrega al FINAL de X-Forwarded-For la IP que ve; las entradas de la
    /// izquierda las controla el cliente (antes se usaba la primera → cada request con un XFF inventado tenía cupo
    /// nuevo). Se toma la entrada que agregó el proxy de confianza más externo (<paramref name="trustedProxyHops"/>
    /// desde la derecha) del header original (<see cref="UseClientIpCapture"/>); sin XFF, la IP de la conexión.
    /// Solo se aceptan IPs válidas y la clave se acota en largo.
    /// </summary>
    public static string ClientKey(HttpContext http, int trustedProxyHops = 1)
    {
        var rawXff = http.Items.TryGetValue(RawForwardedForItem, out var x) && x is string?[] captured
            ? captured
            : http.Request.Headers["X-Forwarded-For"].ToArray();
        var remote = http.Items.TryGetValue(RawRemoteIpItem, out var r) && r is string capturedRemote
            ? capturedRemote
            : http.Connection.RemoteIpAddress?.ToString();

        if (trustedProxyHops > 0)
        {
            var entries = rawXff
                .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();
            if (entries.Length > 0)
            {
                var candidate = entries[Math.Max(0, entries.Length - trustedProxyHops)];
                if (TryNormalizeIp(candidate, out var ip))
                    return Cap(ip);
            }
        }

        if (string.IsNullOrWhiteSpace(remote))
            remote = http.Connection.RemoteIpAddress?.ToString();
        return Cap(string.IsNullOrWhiteSpace(remote) ? "unknown" : remote);
    }

    private static bool TryNormalizeIp(string raw, out string ip)
    {
        ip = string.Empty;
        var value = raw.Trim().Trim('"');
        // "[2001:db8::1]:443" / "203.0.113.5:1234"
        if (value.StartsWith('[') && value.IndexOf(']') is var close and > 0)
            value = value[1..close];
        else if (value.Count(c => c == ':') == 1)
            value = value[..value.IndexOf(':')];

        if (!System.Net.IPAddress.TryParse(value, out var parsed)) return false;
        ip = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4().ToString() : parsed.ToString();
        return true;
    }

    private static string Cap(string key) => key.Length <= MaxPartitionKeyLength ? key : key[..MaxPartitionKeyLength];
}
