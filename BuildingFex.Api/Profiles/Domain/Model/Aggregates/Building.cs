using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Profiles.Domain.Model.Aggregates;

/// <summary>
/// Edificio / condominio registrado por el administrador (RF015 / US015 / TS001).
/// El tenant sigue siendo el admin (<see cref="OwnerAdminId"/> = users.id): cada admin nuevo obtiene un
/// espacio de gestión independiente. Fuera de Portafolio, un admin gestiona un único edificio.
/// </summary>
public class Building : IAuditableEntity
{
    public const int NameMaxLength = 200;
    public const int AddressMaxLength = 500;
    public const int MaxDepartmentCount = 5000;

    public int Id { get; private set; }

    /// <summary>Tenant: users.id del admin dueño.</summary>
    public int OwnerAdminId { get; private set; }

    public string Name { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public int DepartmentCount { get; private set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private Building()
    {
    }

    public static Building Register(int ownerAdminId, string name, string address, int departmentCount)
    {
        var now = DateTimeOffset.UtcNow;
        return new Building
        {
            OwnerAdminId = ownerAdminId,
            Name = name.Trim(),
            Address = address.Trim(),
            DepartmentCount = departmentCount,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Update(string name, string address, int departmentCount)
    {
        Name = name.Trim();
        Address = address.Trim();
        DepartmentCount = departmentCount;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Validación de campos (TS001: 400 con el detalle por campo). Claves = nombres JSON.</summary>
    public static Dictionary<string, string[]> Validate(string? name, string? address, int? departmentCount)
    {
        var errors = new Dictionary<string, string[]>();
        var cleanName = name?.Trim() ?? string.Empty;
        var cleanAddress = address?.Trim() ?? string.Empty;

        if (cleanName.Length == 0)
            errors["name"] = ["El nombre del edificio es obligatorio."];
        else if (cleanName.Length > NameMaxLength)
            errors["name"] = [$"El nombre admite como máximo {NameMaxLength} caracteres."];

        if (cleanAddress.Length == 0)
            errors["address"] = ["La dirección es obligatoria."];
        else if (cleanAddress.Length > AddressMaxLength)
            errors["address"] = [$"La dirección admite como máximo {AddressMaxLength} caracteres."];

        if (departmentCount is null)
            errors["departmentCount"] = ["La cantidad de departamentos es obligatoria."];
        else if (departmentCount < 1 || departmentCount > MaxDepartmentCount)
            errors["departmentCount"] = [$"La cantidad de departamentos debe estar entre 1 y {MaxDepartmentCount}."];

        return errors;
    }
}
