using BuildingFex.Api.Communications.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.Finances.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.Iam.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.Incidents.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.Shared.Infrastructure.Persistence;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.SocialSpaces.Infrastructure.Persistence.Seeding;

namespace BuildingFex.Api.Shared.Infrastructure.Hosting;

/// <summary>
/// Esquema (EnsureCreated + ALTER/CREATE IF NOT EXISTS) y seed opcional desde db.json. Corre al arrancar en
/// local y Railway; en Lambda lo corre el job "migrate".
/// </summary>
public static class DatabaseBootstrap
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration configuration, string contentRootPath)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseBootstrap");

        await SchemaUpgrade.ApplyAsync(context, logger);

        var seedPath = configuration["Seed:DbJsonPath"];
        if (string.IsNullOrWhiteSpace(seedPath))
            return;

        if (!Path.IsPathRooted(seedPath))
            seedPath = Path.GetFullPath(Path.Combine(contentRootPath, seedPath));

        await scope.ServiceProvider.GetRequiredService<DbJsonUserSeeder>().SeedAsync(seedPath);
        await scope.ServiceProvider.GetRequiredService<DbJsonIncidentSeeder>().SeedAsync(seedPath);

        try
        {
            await scope.ServiceProvider.GetRequiredService<DbJsonFinanceSeeder>().SeedAsync(seedPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Finance seed failed — continuing startup.");
        }

        await scope.ServiceProvider.GetRequiredService<DbJsonSocialSpacesSeeder>().SeedAsync(seedPath);
        await scope.ServiceProvider.GetRequiredService<DbJsonAnnouncementSeeder>().SeedAsync(seedPath);
    }
}
