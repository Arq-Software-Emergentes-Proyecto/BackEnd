using System.Text.RegularExpressions;

namespace BuildingFex.Api.Iam.Domain.Model.ValueObjects;

/// <summary>
/// Value Object (Tabla 52): correo validado y normalizado (trim + minúsculas). Dos Email son iguales
/// si su valor normalizado coincide, lo que sostiene la regla "una cuenta por correo".
/// </summary>
public sealed partial record Email
{
    public const int MaxLength = 320;

    public string Value { get; }

    private Email(string value) => Value = value;

    public static string Normalize(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValid(string? raw)
    {
        var value = Normalize(raw);
        return value.Length is > 0 and <= MaxLength && Pattern().IsMatch(value);
    }

    public static bool TryCreate(string? raw, out Email? email)
    {
        email = IsValid(raw) ? new Email(Normalize(raw)) : null;
        return email is not null;
    }

    /// <exception cref="ArgumentException">Si el correo no tiene un formato válido.</exception>
    public static Email Create(string? raw) =>
        TryCreate(raw, out var email)
            ? email!
            : throw new ArgumentException("El correo electrónico no es válido.", nameof(raw));

    public override string ToString() => Value;

    public static implicit operator string(Email email) => email.Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
