namespace BuildingFex.Api.Shared.Infrastructure.Hosting;

/// <summary>
/// AWS Lambda: el mismo binario corre como API (detrás de API Gateway HTTP API) o como función de jobs
/// (tareas programadas por EventBridge Scheduler e indexación de IA). Fuera de Lambda nada de esto aplica.
/// </summary>
public static class LambdaHosting
{
    public const string RoleVariable = "BF_LAMBDA_ROLE";
    public const string JobsFunctionVariable = "BF_JOBS_FUNCTION_NAME";

    /// <summary>
    /// Prefijo opcional de parámetros de SSM Parameter Store (BF_SSM_PATH, p. ej. /buildingfex/). Sin definir no se
    /// usa SSM: en cuentas cuyas políticas de organización lo bloquean, los secretos van en appsettings.Local.json
    /// dentro del paquete (lo escribe el workflow de despliegue).
    /// </summary>
    public static string? ParameterPath =>
        Environment.GetEnvironmentVariable("BF_SSM_PATH") is { Length: > 0 } path ? path : null;

    public static bool UsesParameterStore =>
        IsLambda && ParameterPath is not null && !string.Equals(ParameterPath, "off", StringComparison.OrdinalIgnoreCase);

    public static bool IsLambda =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));

    public static bool IsJobsFunction =>
        IsLambda && string.Equals(Environment.GetEnvironmentVariable(RoleVariable), "jobs", StringComparison.OrdinalIgnoreCase);
}
