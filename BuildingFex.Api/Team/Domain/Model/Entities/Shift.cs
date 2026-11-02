using BuildingFex.Api.Team.Domain.Model;

namespace BuildingFex.Api.Team.Domain.Model.Entities;

/// <summary>Shift (Tabla 80): turno de trabajo de un trabajador. Intervalo semiabierto [start, end).</summary>
public class Shift
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    public int Id { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string OwnerAdminId { get; private set; } = string.Empty;

    /// <summary>team_workers.external_id.</summary>
    public string StaffId { get; private set; } = string.Empty;

    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Shift() { }

    public static Shift Create(
        string externalId,
        string ownerAdminId,
        string staffId,
        DateTimeOffset start,
        DateTimeOffset end,
        string? notes)
    {
        var shift = new Shift
        {
            ExternalId = externalId.Trim(),
            OwnerAdminId = ownerAdminId.Trim(),
            StaffId = (staffId ?? string.Empty).Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        shift.Reschedule(start, end, notes);
        return shift;
    }

    public void Reschedule(DateTimeOffset start, DateTimeOffset end, string? notes)
    {
        EnsureValidRange(start, end);
        StartsAt = start.ToUniversalTime();
        EndsAt = end.ToUniversalTime();
        if (notes is not null)
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public static void EnsureValidRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
            throw new TeamDomainException(TeamError.ShiftInvalidRange, "El fin del turno debe ser posterior al inicio.");
        if (end - start > MaxDuration)
            throw new TeamDomainException(TeamError.ShiftInvalidRange, "Un turno no puede durar más de 24 horas.");
    }

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => StartsAt < end && start < EndsAt;

    public bool Overlaps(Shift other) =>
        string.Equals(StaffId, other.StaffId, StringComparison.Ordinal) && Overlaps(other.StartsAt, other.EndsAt);
}
