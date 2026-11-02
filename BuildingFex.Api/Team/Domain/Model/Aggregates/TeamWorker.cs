using BuildingFex.Api.Team.Domain.Model.ValueObjects;

namespace BuildingFex.Api.Team.Domain.Model.Aggregates;

/// <summary>
/// StaffMember (Tabla 80): trabajador del edificio. Se desactiva en vez de borrarse para conservar
/// el historial de tareas y turnos.
/// </summary>
public class TeamWorker
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string OwnerAdminId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public string Dni { get; private set; } = string.Empty;
    public decimal Salary { get; private set; }
    public string PhotoUrl { get; private set; } = string.Empty;

    /// <summary>CONCIERGE | MAINTENANCE | CLEANING | SECURITY</summary>
    public string Position { get; private set; } = StaffPosition.Default;

    /// <summary>ACTIVE | INACTIVE</summary>
    public string Status { get; private set; } = StaffStatus.Active;

    public DateTimeOffset? DeactivatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsActive => Status != StaffStatus.Inactive;

    private TeamWorker() { }

    public static TeamWorker Create(
        string externalId,
        string ownerAdminId,
        string name,
        string phone,
        string dni,
        decimal salary,
        string photoUrl,
        string? position = null)
    {
        return new TeamWorker
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId,
            Name = name,
            Phone = phone,
            Dni = dni,
            Salary = salary,
            PhotoUrl = photoUrl,
            Position = StaffPosition.Normalize(position) ?? StaffPosition.Default,
            Status = StaffStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void UpdateDetails(string? name, string? phone, string? dni, decimal? salary, string? photoUrl, string? position)
    {
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (phone is not null) Phone = phone.Trim();
        if (dni is not null) Dni = dni.Trim();
        if (salary.HasValue) Salary = salary.Value;
        if (photoUrl is not null) PhotoUrl = photoUrl.Trim();
        if (position is not null)
            Position = StaffPosition.Normalize(position)
                       ?? throw new TeamDomainException(TeamError.InvalidPosition, "Puesto no válido.");
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        Status = StaffStatus.Inactive;
        DeactivatedAt = DateTimeOffset.UtcNow;
    }

    public void Reactivate()
    {
        Status = StaffStatus.Active;
        DeactivatedAt = null;
    }
}
