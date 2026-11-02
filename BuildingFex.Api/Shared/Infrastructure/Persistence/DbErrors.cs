using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Shared.Infrastructure.Persistence;

/// <summary>Clasificación de errores de MySQL detrás de un <see cref="DbUpdateException"/>.</summary>
public static class DbErrors
{
    /// <summary>ER_DUP_ENTRY.</summary>
    public const int DuplicateKey = 1062;

    /// <summary>
    /// ¿Violación de unicidad (1062)? Con <paramref name="indexName"/> solo cuenta si el mensaje de MySQL
    /// ("Duplicate entry 'x' for key 'tabla.indice'") nombra ese índice; así otros errores (FK, columna
    /// truncada, otro índice único) no se confunden con "ya existe".
    /// </summary>
    public static bool IsDuplicateKey(Exception ex, string? indexName = null)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is MySql.Data.MySqlClient.MySqlException { Number: DuplicateKey } my)
            {
                return string.IsNullOrEmpty(indexName)
                       || my.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}
