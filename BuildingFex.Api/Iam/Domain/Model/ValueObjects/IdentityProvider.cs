namespace BuildingFex.Api.Iam.Domain.Model.ValueObjects;

/// <summary>
/// IdentityProvider (Tabla 52): EMAIL_PASSWORD o GOOGLE. Se deriva de users.auth_provider
/// ("password" | "firebase" → EMAIL_PASSWORD, "google" → GOOGLE) sin cambiar los valores guardados.
/// </summary>
public static class IdentityProvider
{
    public const string EmailPassword = "EMAIL_PASSWORD";
    public const string Google = "GOOGLE";

    public static string FromAuthProvider(string? authProvider) =>
        string.Equals((authProvider ?? string.Empty).Trim(), "google", StringComparison.OrdinalIgnoreCase)
            ? Google
            : EmailPassword;
}
