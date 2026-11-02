using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Communications.Application.QueryServices;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Domain.Repositories;

namespace BuildingFex.Api.Communications.Application.Internal.QueryServices;

public class AnnouncementQueryService(
    IAnnouncementRepository announcementRepository,
    IUserRepository userRepository) : IAnnouncementQueryService
{
    public async Task<IEnumerable<Announcement>> ListByOwnerExternalIdAsync(
        string? ownerAdminExternalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerAdminExternalId))
            return await announcementRepository.ListAsync(cancellationToken);

        var owner = await userRepository.FindByExternalIdAsync(ownerAdminExternalId, cancellationToken);
        if (owner is null)
            return [];

        return await announcementRepository.ListByOwnerAdminIdAsync(owner.Id, null, cancellationToken);
    }

    public async Task<IEnumerable<Announcement>> ListActiveByOwnerExternalIdAsync(
        string ownerAdminExternalId,
        CancellationToken cancellationToken = default)
    {
        var owner = await userRepository.FindByExternalIdAsync(ownerAdminExternalId, cancellationToken);
        if (owner is null)
            return [];

        return await announcementRepository.ListByOwnerAdminIdAsync(owner.Id, DateTimeOffset.UtcNow, cancellationToken);
    }
}
