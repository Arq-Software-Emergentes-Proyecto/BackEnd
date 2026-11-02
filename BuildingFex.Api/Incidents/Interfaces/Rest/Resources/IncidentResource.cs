namespace BuildingFex.Api.Incidents.Interfaces.Rest.Resources;

/// <summary>
/// Incidencia (JSON compat json-server). provider = nombre (compat); providerId/Phone/Specialty
/// salen del proveedor registrado para que el residente vea los datos del técnico (US031).
/// events solo se incluye en GET /incidents/{id} (timeline, RNF010).
/// </summary>
public record IncidentResource(
    string Id,
    string Title,
    string? ResidentId,
    string ResidentName,
    string Description,
    string Status,
    string CreatedAt,
    string? Provider,
    string? ProviderId,
    string? ProviderPhone,
    string? ProviderSpecialty,
    string? Category,
    string? Priority,
    string? ResolutionComment,
    string? ResolvedAt,
    string? UpdatedAt,
    string OwnerAdminId,
    IReadOnlyList<IncidentEventResource>? Events);

public record IncidentEventResource(
    int Id,
    string Type,
    string? Status,
    string? Comment,
    string? AuthorId,
    string? AuthorName,
    string? AuthorRole,
    string CreatedAt);

public record CreateIncidentCompatResource(
    string? Id,
    string? Title,
    string? Subject,
    string? ResidentId,
    string? ResidentName,
    string? Description,
    string? Status,
    string? CreatedAt,
    string? Provider,
    string? ProviderId,
    string? Category,
    string? Priority,
    string? OwnerAdminId);

public record UpdateIncidentCompatResource(
    string? Title,
    string? ResidentId,
    string? ResidentName,
    string? Description,
    string? Status,
    string? CreatedAt,
    string? Provider,
    string? ProviderId,
    string? Category,
    string? Priority,
    string? ResolutionComment,
    string? OwnerAdminId);

public record AssignIncidentResource(string? ProviderId, string? Provider, string? Status);

public record ResolveIncidentResource(string? ResolutionComment, string? Comment);

public record AddIncidentCommentResource(string? Comment);
