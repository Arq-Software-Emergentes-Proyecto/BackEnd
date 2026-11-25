namespace BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

/// <summary>Fragmento recuperado de un documento vigente del edificio o de la guía de uso.</summary>
public sealed record KnowledgeSource(string DocumentId, string FileName, string Content, double Score);

/// <summary>Respuesta del asistente junto con la fuente citada (si tuvo base documental).</summary>
public sealed record Answer(string Text, string? SourceDocumentId, string? SourceName, bool Grounded);
