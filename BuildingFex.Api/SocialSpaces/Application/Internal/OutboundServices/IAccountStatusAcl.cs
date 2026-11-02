namespace BuildingFex.Api.SocialSpaces.Application.Internal.OutboundServices;

/// <summary>ACL hacia Finances: ¿el reglamento impide reservar a este residente por morosidad?</summary>
public interface IAccountStatusAcl
{
    Task<bool> BlocksReservationsAsync(int ownerAdminId, string residentExternalId, CancellationToken ct = default);
}
