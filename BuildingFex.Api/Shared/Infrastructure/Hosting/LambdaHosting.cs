namespace BuildingFex.Api.Shared.Infrastructure.Hosting;

/// <summary>
/// Detección de AWS Lambda y de SSM Parameter Store a partir de variables de entorno. Si BF_SSM_PATH no está
/// definido NO se usa SSM: en cuentas donde la organización de AWS lo bloquea, los secretos van en
/// appsettings.Local.json dentro del paquete (lo escribe el workflow de despliegue).
/// </summary>
public static class LambdaHosting
{
    /// <summary>Lambda siempre define AWS_LAMBDA_FUNCTION_NAME; fuera de Lambda es false.</summary>
    public static bool IsLambda =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));

    /// <summary>Prefijo de los parámetros en SSM (p. ej. "/buildingfex/"); null si no está definido.</summary>
    public static string? ParameterPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("BF_SSM_PATH");
            return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        }
    }

    /// <summary>Solo en Lambda, con BF_SSM_PATH definido y distinto de "off".</summary>
    public static bool UsesParameterStore =>
        IsLambda
        && ParameterPath is { } path
        && !path.Equals("off", StringComparison.OrdinalIgnoreCase);
}
