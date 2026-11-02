namespace BuildingFex.Api.Incidents.Domain.Model.Aggregates;

/// <summary>Proveedor de servicio técnico (plomería, etc.) — no confundir con DI ServiceProvider.</summary>
public class BuildingServiceProvider
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string OwnerAdminId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public string Specialty { get; private set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }

    private BuildingServiceProvider() { }

    public static BuildingServiceProvider Create(
        string externalId,
        string ownerAdminId,
        string name,
        string phone,
        string specialty)
    {
        return new BuildingServiceProvider
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId,
            Name = name,
            Phone = phone,
            Specialty = specialty,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
