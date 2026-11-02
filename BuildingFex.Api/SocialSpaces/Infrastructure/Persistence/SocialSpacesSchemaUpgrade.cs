using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.SocialSpaces.Infrastructure.Persistence;

/// <summary>
/// Pasos de datos de SchemaUpgrade para Social Spaces (las columnas/índices se declaran en SchemaUpgrade).
/// </summary>
public static class SocialSpacesSchemaUpgrade
{
    /// <summary>
    /// Rellena reservations.slot_key en filas legadas y reporta turnos activos duplicados.
    /// Devuelve true si es seguro crear el índice único ux_reservations_owner_slot.
    /// </summary>
    public static async Task<bool> PrepareReservationSlotKeysAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE reservations
                SET slot_key = CONCAT(space_external_id, '|', `date`, '|', start_time)
                WHERE slot_key IS NULL AND status <> 'CANCELLED'
                """, ct);

            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var dup = conn.CreateCommand();
            dup.CommandText = """
                SELECT owner_admin_id, slot_key, COUNT(*) AS n, GROUP_CONCAT(external_id ORDER BY id) AS ids
                FROM reservations
                WHERE slot_key IS NOT NULL
                GROUP BY owner_admin_id, slot_key
                HAVING COUNT(*) > 1
                """;
            await using var reader = await dup.ExecuteReaderAsync(ct);
            var duplicates = new List<string>();
            while (await reader.ReadAsync(ct))
                duplicates.Add($"owner={reader.GetValue(0)} slot={reader.GetValue(1)} ids=[{reader.GetValue(3)}]");

            if (duplicates.Count == 0) return true;

            logger.LogWarning(
                "SchemaUpgrade: {Count} reservation slot(s) are double-booked; unique index ux_reservations_owner_slot NOT created " +
                "until they are cancelled/fixed (concurrency is still guarded by the booking lock). {Duplicates}",
                duplicates.Count,
                string.Join("; ", duplicates));
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: reservations.slot_key backfill skipped");
            return false;
        }
    }
}
