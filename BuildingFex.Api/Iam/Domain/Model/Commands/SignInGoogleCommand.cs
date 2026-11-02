namespace BuildingFex.Api.Iam.Domain.Model.Commands;

public record SignInGoogleCommand(
    string Email,
    string GoogleSub,
    string? IdToken = null);
