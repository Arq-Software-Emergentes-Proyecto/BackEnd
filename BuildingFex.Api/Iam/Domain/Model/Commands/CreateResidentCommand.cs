namespace BuildingFex.Api.Iam.Domain.Model.Commands;

public record CreateResidentCommand(
    string ExternalId,
    string Name,
    string Floor,
    string Code,
    string OwnerAdminExternalId,
    string? AdmissionDate,
    bool BillFromNextMonth = false,
    /// <summary>Si se indica, la invitación solo se completa con ese email (importación del padrón, US016).</summary>
    string? InviteEmail = null);
