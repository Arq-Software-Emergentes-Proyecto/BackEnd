namespace BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;

/// <summary>
/// Contador mensual de consultas al asistente de IA por edificio (CON11: tope por plan).
/// Fila única por (owner_admin_id, period); se incrementa de forma atómica con SQL condicional.
/// </summary>
public class AiUsage
{
    public int Id { get; private set; }
    public string OwnerAdminId { get; private set; } = string.Empty;
    /// <summary>Periodo yyyy-MM (hora de Lima).</summary>
    public string Period { get; private set; } = string.Empty;
    public int QueryCount { get; private set; }
    /// <summary>Consultas respondidas sin IA por el filtro de relevancia (saludos, temas ajenos): no consumen el tope.</summary>
    public int FilteredCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private AiUsage()
    {
    }
}
