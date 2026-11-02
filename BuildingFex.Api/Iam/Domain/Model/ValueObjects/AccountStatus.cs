namespace BuildingFex.Api.Iam.Domain.Model.ValueObjects;

/// <summary>Estado de la cuenta (Tabla 52). Una cuenta DEACTIVATED no puede iniciar sesión ni usar la API (403 ACCOUNT_DEACTIVATED).</summary>
public static class AccountStatus
{
    public const string Active = "ACTIVE";
    public const string Deactivated = "DEACTIVATED";

    public const string DeactivatedErrorCode = "ACCOUNT_DEACTIVATED";
}
