namespace BuildingFex.Api.Iam.Domain.Model.Commands;

/// <summary>Desactiva (o reactiva) la cuenta de un residente del condominio del admin.</summary>
public record DeactivateAccountCommand(string ExternalId);

public record ReactivateAccountCommand(string ExternalId);
