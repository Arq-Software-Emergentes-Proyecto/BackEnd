using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Profiles.Domain.Model.Aggregates;

/// <summary>
/// Perfil de negocio del administrador (módulo Profiles).
/// Separado de IAM User (identidad). Relación 1:1 por UserExternalId (= users.external_id).
/// </summary>
public class Profile : IAuditableEntity
{
    public int Id { get; private set; }

    /// <summary>FK real a users.external_id (Firebase uid).</summary>
    public string UserExternalId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string? Dni { get; private set; }
    public string? Address { get; private set; }
    public string? Company { get; private set; }
    public string? Ruc { get; private set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private Profile()
    {
    }

    public static Profile Create(
        string userExternalId,
        string name,
        string email,
        string? dni,
        string? address,
        string? company,
        string? ruc)
    {
        return new Profile
        {
            UserExternalId = userExternalId.Trim(),
            Name = name.Trim(),
            Email = email.Trim().ToLowerInvariant(),
            Dni = NormalizeOptional(dni),
            Address = NormalizeOptional(address),
            Company = NormalizeOptional(company),
            Ruc = NormalizeOptional(ruc),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Update(
        string name,
        string? email,
        string? dni,
        string? address,
        string? company,
        string? ruc)
    {
        if (!string.IsNullOrWhiteSpace(name))
            Name = name.Trim();
        if (!string.IsNullOrWhiteSpace(email))
            Email = email.Trim().ToLowerInvariant();
        Dni = NormalizeOptional(dni);
        Address = NormalizeOptional(address);
        Company = NormalizeOptional(company);
        Ruc = NormalizeOptional(ruc);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
