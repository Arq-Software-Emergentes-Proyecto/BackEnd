namespace BuildingFex.Api.Communications.Domain.Model.Aggregates;

public class ImportFolder
{
    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string OwnerAdminId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsHome { get; private set; }
    /// <summary>Carpeta de reglas/contexto para el asistente (RAG).</summary>
    public bool IsContext { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }

    private ImportFolder() { }

    public static ImportFolder Create(
        string externalId,
        string ownerAdminId,
        string name,
        bool isHome,
        bool isContext = false)
    {
        return new ImportFolder
        {
            ExternalId = externalId,
            OwnerAdminId = ownerAdminId,
            Name = name,
            IsHome = isHome,
            IsContext = isContext,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Patch(string? name, bool? isHome, bool? isContext = null)
    {
        if (name is not null)
            Name = name;
        if (isHome.HasValue)
            IsHome = isHome.Value;
        if (isContext.HasValue)
            IsContext = isContext.Value;
    }
}
