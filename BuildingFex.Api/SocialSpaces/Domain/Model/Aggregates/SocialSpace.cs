using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Model.Entities;
using BuildingFex.Api.SocialSpaces.Domain.Model.ValueObjects;

namespace BuildingFex.Api.SocialSpaces.Domain.Model.Aggregates;

public class SocialSpace : IAuditableEntity
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public int OwnerAdminId { get; private set; }
    public User? OwnerAdmin { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public int? Capacity { get; private set; }
    public string ImageUrl { get; private set; } = string.Empty;

    /// <summary>Reglamento de uso visible en el catálogo (Tabla 68: rules).</summary>
    public string Rules { get; private set; } = string.Empty;

    /// <summary>AVAILABLE o MAINTENANCE; en mantenimiento no admite reservas nuevas.</summary>
    public string Status { get; private set; } = SocialSpaceStatus.Available;

    /// <summary>Cupo de reservas por día (null = sin límite, solo traslapes).</summary>
    public int? MaxReservationsPerDay { get; private set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private SocialSpace() { }

    public static SocialSpace Create(
        string externalId,
        int ownerAdminId,
        string name,
        string description,
        int? capacity,
        string imageUrl)
    {
        return new SocialSpace
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId,
            Name = name,
            Description = description,
            Capacity = capacity,
            ImageUrl = imageUrl,
        };
    }

    public void Patch(string name, string description, int? capacity, string? imageUrl)
    {
        Name = name;
        Description = description;
        Capacity = capacity;
        if (imageUrl is not null)
            ImageUrl = imageUrl;
    }

    public void SetRules(string? rules) => Rules = (rules ?? string.Empty).Trim();

    public void SetMaxReservationsPerDay(int? max) => MaxReservationsPerDay = max is > 0 ? max : null;

    public bool IsAvailable() => !string.Equals(Status, SocialSpaceStatus.Maintenance, StringComparison.Ordinal);

    public void SetUnderMaintenance() => Status = SocialSpaceStatus.Maintenance;

    public void SetAvailable() => Status = SocialSpaceStatus.Available;

    public void SetStatus(string? status)
    {
        if (SocialSpaceStatus.Normalize(status) == SocialSpaceStatus.Maintenance) SetUnderMaintenance();
        else SetAvailable();
    }
}
