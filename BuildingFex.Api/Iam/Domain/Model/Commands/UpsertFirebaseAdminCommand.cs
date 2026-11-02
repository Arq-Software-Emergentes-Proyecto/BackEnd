namespace BuildingFex.Api.Iam.Domain.Model.Commands;

public record UpsertFirebaseAdminCommand(
    string ExternalId,
    string Name,
    string Email,
    string? Dni = null,
    string? Address = null,
    string? Company = null,
    string? Ruc = null);
