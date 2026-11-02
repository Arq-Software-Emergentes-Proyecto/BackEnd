namespace BuildingFex.Api.Finances.Domain.Model.Aggregates;

public class ResidentExtraCharge
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string OwnerAdminId { get; private set; } = string.Empty;
    public string ResidentId { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    private ResidentExtraCharge() { }

    public static ResidentExtraCharge Create(
        string externalId,
        string ownerAdminId,
        string residentId,
        string title,
        decimal amount,
        string description)
    {
        return new ResidentExtraCharge
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId,
            ResidentId = residentId,
            Title = title,
            Amount = amount,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
