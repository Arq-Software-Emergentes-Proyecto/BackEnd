using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Incidents.Application.CommandServices;
using BuildingFex.Api.Incidents.Application.Internal.OutboundServices;
using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model.Commands;
using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;
using BuildingFex.Api.Incidents.Domain.Repositories;
using BuildingFex.Api.Shared.Application.Model;
using BuildingFex.Api.Shared.Domain.Repositories;

namespace BuildingFex.Api.Incidents.Application.Internal.CommandServices;

public class IncidentCommandService(
    IIncidentRepository incidentRepository,
    IServiceProviderRepository serviceProviderRepository,
    IUserRepository userRepository,
    IIncidentResolutionNotifier resolutionNotifier,
    IUnitOfWork unitOfWork,
    ILogger<IncidentCommandService> logger) : IIncidentCommandService
{
    /// <summary>ReportIncident: asunto + descripción obligatorios (RF024).</summary>
    public async Task<Result<Incident>> Handle(
        CreateIncidentCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Title))
            return Result<Incident>.Failure(IncidentError.TitleRequired, "El asunto es obligatorio.");

        if (string.IsNullOrWhiteSpace(command.Description))
            return Result<Incident>.Failure(IncidentError.DescriptionRequired, "La descripción es obligatoria.");

        if (string.IsNullOrWhiteSpace(command.OwnerAdminExternalId))
            return Result<Incident>.Failure(IncidentError.OwnerAdminRequired, "ownerAdminId es obligatorio.");

        var owner = await userRepository.FindByExternalIdAsync(command.OwnerAdminExternalId, cancellationToken);
        if (owner is null || owner.Role != "admin")
            return Result<Incident>.Failure(IncidentError.OwnerAdminRequired, "Administrador no encontrado.");

        DateTimeOffset? reportedAt = null;
        if (!string.IsNullOrWhiteSpace(command.CreatedAt) &&
            DateTimeOffset.TryParse(command.CreatedAt, out var parsed))
            reportedAt = parsed;

        var externalId = string.IsNullOrWhiteSpace(command.ExternalId)
            ? $"incident-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : command.ExternalId.Trim();

        var (assigneeId, providerName, providerError) = await ResolveProviderAsync(
            owner.ExternalId, command.ProviderExternalId, command.Provider, cancellationToken);
        if (providerError is not null) return providerError;

        Incident incident;
        try
        {
            incident = Incident.Create(
                externalId,
                owner.Id,
                command.Description,
                command.Status,
                command.ResidentExternalId,
                command.ResidentName,
                providerName,
                reportedAt,
                command.Title,
                command.Category,
                command.Priority,
                assigneeId,
                command.Actor);
            incident.LinkSourceChat(command.SourceChatId);
        }
        catch (IncidentDomainException ex)
        {
            return Result<Incident>.Failure(ex.Error, ex.Message);
        }

        await incidentRepository.AddAsync(incident, cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);

        var created = await incidentRepository.FindByExternalIdAsync(externalId, cancellationToken);
        return Result<Incident>.Success(created!);
    }

    /// <summary>PUT compat: detalles + proveedor + estado. Transición a resuelta exige comentario.</summary>
    public async Task<Result<Incident>> Handle(
        UpdateIncidentCommand command,
        CancellationToken cancellationToken = default)
    {
        var incident = await incidentRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (incident is null)
            return Result<Incident>.Failure(IncidentError.IncidentNotFound, "Incidencia no encontrada.");

        if (string.IsNullOrWhiteSpace(command.Description))
            return Result<Incident>.Failure(IncidentError.DescriptionRequired, "La descripción es obligatoria.");

        var actor = command.Actor ?? IncidentActor.System;
        var wasResolved = incident.Status == IncidentStatuses.Resolved;
        try
        {
            incident.UpdateDetails(
                command.Title,
                command.Description,
                command.Category,
                command.Priority,
                command.ResidentExternalId,
                command.ResidentName);

            // Proveedor: solo si el cliente envió el campo (null = sin cambios).
            if (command.ProviderExternalId is not null || command.Provider is not null)
            {
                var ownerExternalId = incident.OwnerAdmin?.ExternalId ?? string.Empty;
                var (assigneeId, providerName, providerError) = await ResolveProviderAsync(
                    ownerExternalId, command.ProviderExternalId, command.Provider, cancellationToken);
                if (providerError is not null) return providerError;
                incident.Assign(assigneeId, providerName, actor, advanceStatus: false);
            }

            incident.ChangeStatus(command.Status, command.ResolutionComment, actor);
        }
        catch (IncidentDomainException ex)
        {
            return Result<Incident>.Failure(ex.Error, ex.Message);
        }

        var saved = await SaveAsync(incident, cancellationToken);
        if (!wasResolved && incident.Status == IncidentStatuses.Resolved)
            await NotifyResolvedAsync(incident, cancellationToken);
        return saved;
    }

    public async Task<Result<Incident>> Handle(
        AssignIncidentCommand command,
        CancellationToken cancellationToken = default)
    {
        var incident = await incidentRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (incident is null)
            return Result<Incident>.Failure(IncidentError.IncidentNotFound, "Incidencia no encontrada.");

        var ownerExternalId = incident.OwnerAdmin?.ExternalId ?? string.Empty;
        var (assigneeId, providerName, providerError) = await ResolveProviderAsync(
            ownerExternalId, command.ProviderExternalId, command.ProviderName, cancellationToken);
        if (providerError is not null) return providerError;

        try
        {
            var explicitStatus = !string.IsNullOrWhiteSpace(command.Status);
            incident.Assign(assigneeId, providerName, command.Actor, advanceStatus: !explicitStatus);
            if (explicitStatus)
                incident.ChangeStatus(command.Status!, null, command.Actor);
        }
        catch (IncidentDomainException ex)
        {
            return Result<Incident>.Failure(ex.Error, ex.Message);
        }

        return await SaveAsync(incident, cancellationToken);
    }

    public async Task<Result<Incident>> Handle(
        ResolveIncidentCommand command,
        CancellationToken cancellationToken = default)
    {
        var incident = await incidentRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (incident is null)
            return Result<Incident>.Failure(IncidentError.IncidentNotFound, "Incidencia no encontrada.");

        try
        {
            incident.Resolve(command.ResolutionComment, command.Actor);
        }
        catch (IncidentDomainException ex)
        {
            return Result<Incident>.Failure(ex.Error, ex.Message);
        }

        var saved = await SaveAsync(incident, cancellationToken);
        await NotifyResolvedAsync(incident, cancellationToken);
        return saved;
    }

    public async Task<Result<Incident>> Handle(
        AddIncidentCommentCommand command,
        CancellationToken cancellationToken = default)
    {
        var incident = await incidentRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (incident is null)
            return Result<Incident>.Failure(IncidentError.IncidentNotFound, "Incidencia no encontrada.");

        try
        {
            incident.AddComment(command.Comment, command.Actor);
        }
        catch (IncidentDomainException ex)
        {
            return Result<Incident>.Failure(ex.Error, ex.Message);
        }

        return await SaveAsync(incident, cancellationToken);
    }

    public async Task<Result<Incident>> Handle(
        DeleteIncidentCommand command,
        CancellationToken cancellationToken = default)
    {
        var incident = await incidentRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (incident is null)
            return Result<Incident>.Failure(IncidentError.IncidentNotFound, "Incidencia no encontrada.");

        incidentRepository.Remove(incident);
        await unitOfWork.CompleteAsync(cancellationToken);

        return Result<Incident>.Success(incident);
    }

    /// <summary>IncidenteResuelto → Resident Care. Mejor esfuerzo: la resolución ya quedó guardada.</summary>
    private async Task NotifyResolvedAsync(Incident incident, CancellationToken cancellationToken)
    {
        try
        {
            await resolutionNotifier.NotifyResolvedAsync(incident, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Resolution notice failed for incident {Incident}", incident.ExternalId);
        }
    }

    private async Task<Result<Incident>> SaveAsync(Incident incident, CancellationToken cancellationToken)
    {
        // La entidad ya está trackeada (FindByExternalIdAsync); los eventos nuevos se insertan por DetectChanges.
        await unitOfWork.CompleteAsync(cancellationToken);
        var updated = await incidentRepository.FindByExternalIdAsync(incident.ExternalId, cancellationToken);
        return Result<Incident>.Success(updated!);
    }

    /// <summary>
    /// providerId (assigneeId) tiene prioridad: debe existir en el condominio y fija el nombre.
    /// Sin id, se intenta casar el nombre legacy con un proveedor registrado; si no existe, se guarda solo el nombre.
    /// </summary>
    private async Task<(string? AssigneeId, string? ProviderName, Result<Incident>? Error)> ResolveProviderAsync(
        string ownerAdminExternalId,
        string? providerExternalId,
        string? providerName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(providerExternalId))
        {
            var provider = await serviceProviderRepository.FindByExternalIdAsync(
                ownerAdminExternalId, providerExternalId, cancellationToken);
            if (provider is null)
                return (null, null, Result<Incident>.Failure(IncidentError.ProviderNotFound, "Proveedor no encontrado en este condominio."));
            return (provider.ExternalId, provider.Name, null);
        }

        if (string.IsNullOrWhiteSpace(providerName))
            return (null, null, null);

        var name = providerName.Trim();
        var providers = await serviceProviderRepository.ListByOwnerAsync(ownerAdminExternalId, cancellationToken);
        var match = providers.FirstOrDefault(p => string.Equals(p.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        return (match?.ExternalId, match?.Name ?? name, null);
    }
}
