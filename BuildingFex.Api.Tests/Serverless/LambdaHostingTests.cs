using BuildingFex.Api.Shared.Infrastructure.Hosting;

namespace BuildingFex.Api.Tests.Serverless;

/// <summary>Las variables de entorno son del proceso: estas pruebas no corren en paralelo con otras de la colección.</summary>
[Collection(nameof(LambdaEnvironment))]
public class LambdaHostingTests
{
    [Fact]
    public void Outside_lambda_nothing_changes()
    {
        using var env = new LambdaEnvironment(functionName: null, role: null);

        Assert.False(LambdaHosting.IsLambda);
        Assert.False(LambdaHosting.IsJobsFunction);
        Assert.False(LambdaHosting.UsesParameterStore);
    }

    [Fact]
    public void Jobs_role_runs_the_jobs_function()
    {
        using var env = new LambdaEnvironment(functionName: "buildingfex-jobs", role: "jobs");

        Assert.True(LambdaHosting.IsLambda);
        Assert.True(LambdaHosting.IsJobsFunction);
    }

    [Fact]
    public void Api_role_serves_http()
    {
        using var env = new LambdaEnvironment(functionName: "buildingfex-api", role: "api");

        Assert.True(LambdaHosting.IsLambda);
        Assert.False(LambdaHosting.IsJobsFunction);
    }
}

[CollectionDefinition(nameof(LambdaEnvironment), DisableParallelization = true)]
public sealed class LambdaEnvironment : IDisposable
{
    private readonly string? _function = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME");
    private readonly string? _role = Environment.GetEnvironmentVariable(LambdaHosting.RoleVariable);

    public LambdaEnvironment() : this(null, null)
    {
    }

    public LambdaEnvironment(string? functionName, string? role)
    {
        Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", functionName);
        Environment.SetEnvironmentVariable(LambdaHosting.RoleVariable, role);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", _function);
        Environment.SetEnvironmentVariable(LambdaHosting.RoleVariable, _role);
    }
}
