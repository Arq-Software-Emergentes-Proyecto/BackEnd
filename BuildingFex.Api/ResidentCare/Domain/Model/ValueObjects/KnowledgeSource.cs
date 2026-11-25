namespace BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

/// <summary>
/// Fragmento recuperado para una consulta: un chunk de un documento vigente del edificio
/// (carpeta Contexto IA) o una sección de la guía de uso de BuildingFex.
/// </summary>
public sealed record KnowledgeSource(string DocumentId, string FileName, string Content, double Score);

/// <summary>Respuesta generada con su fuente citada (informe 5.9.1: text, sourceDocumentId, grounded).</summary>
public sealed record Answer(
    string Text,
    string? SourceDocumentId,
    string? SourceName,
    bool Grounded,
    bool MustDerive);
