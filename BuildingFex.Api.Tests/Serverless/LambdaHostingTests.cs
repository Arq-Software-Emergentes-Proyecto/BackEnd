using BuildingFex.Api.Shared.Infrastructure.Hosting;
using Xunit;

namespace BuildingFex.Api.Tests.Serverless;

[CollectionDefinition(nameof(LambdaEnvironment), DisableParallelization = true)]
public class LambdaEnvironmentDefinition : ICollectionFixture<LambdaEnvironmentFixture>
{
}

public class LambdaEnvironmentFixture
{
}

public sealed class LambdaEnvironment : IDisposable
{
    private readonly string? _originalFunctionName;
    private readonly string? _originalRole;

    public LambdaEnvironment(string? functionName = null, string? role = null)
    {
        _originalFunctionName = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME");
        _originalRole = Environment.GetEnvironmentVariable(LambdaHosting.RoleVariable);

        Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", functionName);
        Environment.SetEnvironmentVariable(LambdaHosting.RoleVariable, role);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", _originalFunctionName);
        Environment.SetEnvironmentVariable(LambdaHosting.RoleVariable, _originalRole);
    }
}

[Collection(nameof(LambdaEnvironment))]
public class LambdaHostingTests
{
    [Fact]
    public void Outside_lambda_nothing_changes()
    {
        using var env = new LambdaEnvironment(null, null);

        Assert.False(LambdaHosting.IsLambda);
        Assert.False(LambdaHosting.IsJobsFunction);
        Assert.False(LambdaHosting.UsesParameterStore);
    }

    [Fact]
    public void Jobs_role_runs_the_jobs_function()
    {
        using var env = new LambdaEnvironment("buildingfex-jobs", "jobs");

        Assert.True(LambdaHosting.IsLambda);
        Assert.True(LambdaHosting.IsJobsFunction);
    }

    [Fact]
    public void Api_role_serves_http()
    {
        using var env = new LambdaEnvironment("buildingfex-api", "api");

        Assert.True(LambdaHosting.IsLambda);
        Assert.False(LambdaHosting.IsJobsFunction);
    }
}
