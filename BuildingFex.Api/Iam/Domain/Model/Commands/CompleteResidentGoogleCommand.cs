namespace BuildingFex.Api.Iam.Domain.Model.Commands;

public record CompleteResidentGoogleCommand(
    string Code,
    string Email,
    string GoogleSub,
    string? Name = null,
    string? IdToken = null);
