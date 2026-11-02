using BuildingFex.Api.Finances.Application.Internal.Delinquency;
using BuildingFex.Api.SocialSpaces.Application.Internal.OutboundServices;

namespace BuildingFex.Api.SocialSpaces.Infrastructure.Acl;

/// <summary>Traduce el estado de cuenta de Finances a la única pregunta que le importa a Social Spaces.</summary>
public class FinancesAccountStatusAcl(IAccountStatusQueryService accountStatus) : IAccountStatusAcl
{
    public async Task<bool> BlocksReservationsAsync(int ownerAdminId, string residentExternalId, CancellationToken ct = default) =>
        (await accountStatus.GetAsync(ownerAdminId, residentExternalId, ct)).RestrictsReservations;
}
