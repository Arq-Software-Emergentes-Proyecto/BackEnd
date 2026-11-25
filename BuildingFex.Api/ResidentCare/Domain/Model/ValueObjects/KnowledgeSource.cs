namespace BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;

/// <summary>Fragmento recuperado de un documento vigente del edificio o de la guía de uso.</summary>
public sealed record KnowledgeSource(string DocumentId, string FileName, string Content, double Score);
