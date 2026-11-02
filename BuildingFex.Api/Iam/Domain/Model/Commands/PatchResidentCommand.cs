namespace BuildingFex.Api.Iam.Domain.Model.Commands;

public record PatchResidentCommand(
    string ExternalId,
    string? Email = null,
    string? Password = null,
    bool? BillFromNextMonth = null,
    string? Name = null,
    string? Floor = null,
    string? Code = null);
