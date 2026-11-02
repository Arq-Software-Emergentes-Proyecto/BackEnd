using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Communications.Application.CommandServices;
using BuildingFex.Api.Communications.Domain.Model;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Domain.Model.Commands;
using BuildingFex.Api.Communications.Domain.Repositories;
using BuildingFex.Api.Shared.Application.Model;
using BuildingFex.Api.Shared.Domain.Repositories;

namespace BuildingFex.Api.Communications.Application.Internal.CommandServices;

public class AnnouncementCommandService(
    IAnnouncementRepository announcementRepository,
    IUserRepository userRepository,
    IUnitOfWork unitOfWork) : IAnnouncementCommandService
{
    public async Task<Result<Announcement>> Handle(
        CreateAnnouncementCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Title))
            return Result<Announcement>.Failure(AnnouncementError.TitleRequired, "El título es obligatorio.");

        if (string.IsNullOrWhiteSpace(command.OwnerAdminExternalId))
            return Result<Announcement>.Failure(AnnouncementError.OwnerAdminRequired, "ownerAdminId es obligatorio.");

        var owner = await userRepository.FindByExternalIdAsync(command.OwnerAdminExternalId, cancellationToken);
        if (owner is null || owner.Role != "admin")
            return Result<Announcement>.Failure(AnnouncementError.OwnerAdminRequired, "Administrador no encontrado.");

        if (!Announcement.IsValidDuration(command.Duration))
            return Result<Announcement>.Failure(AnnouncementError.InvalidDuration, "La duración debe estar entre 1 y 365 días.");

        // RF019: CreatedAt lo fija el servidor y ExpiresAt = CreatedAt + Duration (en el dominio).
        // command.CreatedAt / command.ExpiresAt del cliente se ignoran (evita anuncios "eternos" o retro-fechados).
        var externalId = string.IsNullOrWhiteSpace(command.ExternalId)
            ? $"ann-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : command.ExternalId.Trim();

        var announcement = Announcement.Create(
            externalId,
            owner.Id,
            command.Title,
            command.Body,
            command.Priority,
            command.Duration,
            command.AuthorName);

        await announcementRepository.AddAsync(announcement, cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);

        var created = await announcementRepository.FindByExternalIdAsync(externalId, cancellationToken);
        return Result<Announcement>.Success(created!);
    }

    public async Task<Result<Announcement>> Handle(
        UpdateAnnouncementCommand command,
        CancellationToken cancellationToken = default)
    {
        var announcement = await announcementRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (announcement is null)
            return Result<Announcement>.Failure(AnnouncementError.AnnouncementNotFound, "Comunicado no encontrado.");

        if (string.IsNullOrWhiteSpace(command.Title))
            return Result<Announcement>.Failure(AnnouncementError.TitleRequired, "El título es obligatorio.");

        if (!Announcement.IsValidDuration(command.Duration))
            return Result<Announcement>.Failure(AnnouncementError.InvalidDuration, "La duración debe estar entre 1 y 365 días.");

        announcement.Update(
            command.Title,
            command.Body,
            command.Priority,
            command.Duration);

        announcementRepository.Update(announcement);
        await unitOfWork.CompleteAsync(cancellationToken);

        var updated = await announcementRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        return Result<Announcement>.Success(updated!);
    }

    public async Task<Result<Announcement>> Handle(
        DeleteAnnouncementCommand command,
        CancellationToken cancellationToken = default)
    {
        var announcement = await announcementRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (announcement is null)
            return Result<Announcement>.Failure(AnnouncementError.AnnouncementNotFound, "Comunicado no encontrado.");

        announcementRepository.Remove(announcement);
        await unitOfWork.CompleteAsync(cancellationToken);

        return Result<Announcement>.Success(announcement);
    }
}
