using System.Text;
using BuildingFex.Api.Iam.Application.CommandServices;
using BuildingFex.Api.Iam.Application.Internal.CommandServices;
using BuildingFex.Api.Iam.Application.Internal.OutboundServices;
using BuildingFex.Api.Iam.Application.Internal.QueryServices;
using BuildingFex.Api.Iam.Application.QueryServices;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Iam.Infrastructure.Hashing.BCrypt.Services;
using BuildingFex.Api.Iam.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using BuildingFex.Api.Iam.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.Incidents.Application.CommandServices;
using BuildingFex.Api.Incidents.Application.Internal.CommandServices;
using BuildingFex.Api.Incidents.Application.Internal.QueryServices;
using BuildingFex.Api.Incidents.Application.QueryServices;
using BuildingFex.Api.Incidents.Domain.Repositories;
using BuildingFex.Api.Incidents.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using BuildingFex.Api.Finances.Application.Internal;
using BuildingFex.Api.Finances.Application.Internal.Dashboard;
using BuildingFex.Api.Finances.Application.Internal.MercadoPago;
using BuildingFex.Api.Finances.Domain.Repositories;
using BuildingFex.Api.Finances.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using BuildingFex.Api.Finances.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.Incidents.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.Communications.Application.CommandServices;
using BuildingFex.Api.Communications.Application.Internal.CommandServices;
using BuildingFex.Api.Communications.Application.Internal.QueryServices;
using BuildingFex.Api.Communications.Application.QueryServices;
using BuildingFex.Api.Communications.Domain.Repositories;
using BuildingFex.Api.Communications.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using BuildingFex.Api.Communications.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.SocialSpaces.Application.Internal;
using BuildingFex.Api.SocialSpaces.Domain.Repositories;
using BuildingFex.Api.SocialSpaces.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using BuildingFex.Api.SocialSpaces.Infrastructure.Persistence.Seeding;
using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt;
using BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt.Configuration;
using BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt.Services;
using BuildingFex.Api.Shared.Infrastructure.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Hosting;
using BuildingFex.Api.Shared.Infrastructure.RateLimiting;
using BuildingFex.Api.Shared.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
// SSM Parameter Store: /buildingfex/Seccion/Clave → Seccion:Clave.
if (LambdaHosting.UsesParameterStore)
    builder.Configuration.AddSystemsManager(LambdaHosting.ParameterPath!);
RailwayHosting.ConfigureKestrelPort(builder);
RailwayHosting.ApplySecretsFromEnvironment(builder);
RailwayHosting.ValidateProductionSecrets(builder.Configuration, builder.Environment);
// No hace nada fuera de Lambda.
if (!LambdaHosting.IsJobsFunction)
    builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var tokenSettings = builder.Configuration.GetSection("TokenSettings").Get<TokenSettings>()
    ?? throw new InvalidOperationException("TokenSettings section is missing.");

var firebaseSettings = builder.Configuration.GetSection(FirebaseAuthSettings.SectionName).Get<FirebaseAuthSettings>()
    ?? new FirebaseAuthSettings();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "BuildingFex API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme (Firebase ID token).",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        BearerFormat = "JWT",
        Scheme = "bearer",
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("bearer", document)] = [],
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        // Content-Disposition: el FE lee el nombre de archivo de PDFs / plantillas descargados cross-origin.
        policy.WithExposedHeaders("Content-Disposition");
        var allowedOrigins = builder.Configuration["Cors:AllowedOrigins"];
        if (!string.IsNullOrWhiteSpace(allowedOrigins))
        {
            policy.WithOrigins(allowedOrigins
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .AllowAnyMethod()
                .AllowAnyHeader();
            return;
        }

        if (builder.Environment.IsDevelopment())
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        else
            policy.SetIsOriginAllowed(_ => true).AllowAnyMethod().AllowAnyHeader();
    });
});

builder.Services.Configure<TokenSettings>(builder.Configuration.GetSection("TokenSettings"));
builder.Services.Configure<FirebaseAuthSettings>(builder.Configuration.GetSection(FirebaseAuthSettings.SectionName));
builder.Services.Configure<BuildingFex.Api.ResidentCare.Infrastructure.Configuration.GeminiSettings>(
    builder.Configuration.GetSection(BuildingFex.Api.ResidentCare.Infrastructure.Configuration.GeminiSettings.SectionName));
builder.Services.AddSingleton<BuildingFex.Api.ResidentCare.Infrastructure.RateLimiting.GeminiRateLimiter>();
builder.Services.AddHttpClient<BuildingFex.Api.ResidentCare.Infrastructure.Gemini.IGeminiClient,
    BuildingFex.Api.ResidentCare.Infrastructure.Gemini.GeminiClient>();
builder.Services.AddHttpClient<BuildingFex.Api.ResidentCare.Infrastructure.Gemini.IGeminiEmbeddingClient,
    BuildingFex.Api.ResidentCare.Infrastructure.Gemini.GeminiEmbeddingClient>();
builder.Services.AddScoped<BuildingFex.Api.ResidentCare.Application.IAiContextIndexer,
    BuildingFex.Api.ResidentCare.Application.AiContextIndexer>();
builder.Services.AddScoped<BuildingFex.Api.ResidentCare.Application.AiContextIndexingJob>();
builder.Services.Configure<BuildingFex.Api.Communications.Infrastructure.Configuration.ImportSettings>(
    builder.Configuration.GetSection(BuildingFex.Api.Communications.Infrastructure.Configuration.ImportSettings.SectionName));
builder.Services.Configure<BuildingFex.Api.Communications.Infrastructure.Configuration.FirebaseStorageSettings>(
    builder.Configuration.GetSection(BuildingFex.Api.Communications.Infrastructure.Configuration.FirebaseStorageSettings.SectionName));
// Archivos: Amazon S3 cuando el despliegue define BF_FILES_BUCKET (AWS); si no, Firebase Storage.
if (BuildingFex.Api.Communications.Infrastructure.Storage.S3StorageService.ConfiguredBucket is { } filesBucket)
{
    builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(_ => new Amazon.S3.AmazonS3Client());
    builder.Services.AddSingleton<BuildingFex.Api.Communications.Infrastructure.Storage.IFirebaseStorageService>(sp =>
        new BuildingFex.Api.Communications.Infrastructure.Storage.S3StorageService(
            sp.GetRequiredService<Amazon.S3.IAmazonS3>(),
            filesBucket));
}
else
{
    builder.Services.AddSingleton<BuildingFex.Api.Communications.Infrastructure.Storage.IFirebaseStorageService,
        BuildingFex.Api.Communications.Infrastructure.Storage.FirebaseStorageService>();
}
builder.Services.AddScoped<BuildingFex.Api.Communications.Infrastructure.ImportOrphanCleanup>();
// En Lambda los trabajos en segundo plano corren vía JobsFunction ("import-cleanup", "ai-index").
if (LambdaHosting.IsLambda)
{
    builder.Services.AddSingleton<Amazon.Lambda.IAmazonLambda>(_ => new Amazon.Lambda.AmazonLambdaClient());
    builder.Services.AddSingleton<BuildingFex.Api.ResidentCare.Application.IAiContextIndexingQueue,
        BuildingFex.Api.ResidentCare.Infrastructure.Lambda.LambdaAiContextIndexingQueue>();
}
else
{
    builder.Services.AddSingleton<BuildingFex.Api.ResidentCare.Application.IAiContextIndexingQueue,
        BuildingFex.Api.ResidentCare.Application.AiContextIndexingQueue>();
    builder.Services.AddHostedService<BuildingFex.Api.ResidentCare.Application.AiContextIndexingWorker>();
    builder.Services.AddHostedService<BuildingFex.Api.Communications.Infrastructure.ImportOrphanCleanupWorker>();
}
builder.Services.AddSignalR();

builder.Services.AddBuildingFexAuthentication(tokenSettings, firebaseSettings);
builder.Services.AddBuildingFexAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICallerContextAccessor, CallerContextAccessor>();
builder.Services.AddBuildingFexRateLimiting(builder.Configuration.GetValue("RateLimiting:TrustedProxyHops", 1));

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = RailwayHosting.ResolveConnectionString(builder.Configuration);
    options.UseMySQL(connectionString);
});

// Shared
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// IAM
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserCommandService, UserCommandService>();
builder.Services.AddScoped<IUserQueryService, UserQueryService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IHashingService, HashingService>();
builder.Services.AddSingleton<BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt.IFirebaseIdTokenVerifier,
    BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt.FirebaseIdTokenVerifier>();
builder.Services.AddSingleton<BuildingFex.Api.Iam.Infrastructure.Firebase.IFirebaseAuthAdminService,
    BuildingFex.Api.Iam.Infrastructure.Firebase.FirebaseAuthAdminService>();
builder.Services.AddScoped<DbJsonUserSeeder>();

// Billing (subscriptions, trial, plan limits) + Profiles (buildings, roster import)
builder.Services.AddScoped<BuildingFex.Api.Billing.Domain.Repositories.ISubscriptionRepository,
    BuildingFex.Api.Billing.Infrastructure.Persistence.EntityFrameworkCore.Repositories.SubscriptionRepository>();
builder.Services.AddScoped<BuildingFex.Api.Billing.Application.Internal.ISubscriptionService,
    BuildingFex.Api.Billing.Application.Internal.SubscriptionService>();
builder.Services.AddScoped<BuildingFex.Api.Profiles.Domain.Repositories.IBuildingRepository,
    BuildingFex.Api.Profiles.Infrastructure.Persistence.EntityFrameworkCore.Repositories.BuildingRepository>();
builder.Services.AddScoped<BuildingFex.Api.Profiles.Application.Internal.ResidentRosterImportService>();

// Incidents
builder.Services.AddScoped<IIncidentRepository, IncidentRepository>();
builder.Services.AddScoped<IServiceProviderRepository, ServiceProviderRepository>();
builder.Services.AddScoped<IIncidentCommandService, IncidentCommandService>();
builder.Services.AddScoped<IIncidentQueryService, IncidentQueryService>();
builder.Services.AddScoped<DbJsonIncidentSeeder>();
builder.Services.AddScoped<BuildingFex.Api.Incidents.Application.Internal.OutboundServices.IIncidentResolutionNotifier,
    BuildingFex.Api.ResidentCare.Application.IncidentResolutionNotifier>();

// Finances
builder.Services.AddScoped<IFeeRepository, FeeRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IReceiptRepository, ReceiptRepository>();
builder.Services.AddScoped<IFinanceSettingRepository, FinanceSettingRepository>();
builder.Services.AddScoped<IKpiRepository, KpiRepository>();
builder.Services.AddScoped<IAdminManagementExpenseRepository, AdminManagementExpenseRepository>();
builder.Services.AddScoped<ISharedUtilityServiceRepository, SharedUtilityServiceRepository>();
builder.Services.AddScoped<IFixedPayoutRecipientRepository, FixedPayoutRecipientRepository>();
builder.Services.AddScoped<IMaintenanceCheckoutRepository, MaintenanceCheckoutRepository>();
builder.Services.AddScoped<FinanceOwnerResolver>();
builder.Services.AddScoped<IDashboardQueryService, DashboardQueryService>();
builder.Services.AddScoped<BuildingFex.Api.Finances.Application.Internal.Billing.IMonthlyBillingService,
    BuildingFex.Api.Finances.Application.Internal.Billing.MonthlyBillingService>();
builder.Services.AddScoped<BuildingFex.Api.Finances.Application.Internal.Delinquency.IPaymentCalendarQueryService,
    BuildingFex.Api.Finances.Application.Internal.Delinquency.PaymentCalendarQueryService>();
builder.Services.AddSingleton<BuildingFex.Api.Finances.Application.Internal.ReceiptDocuments.IReceiptPdfRenderer,
    BuildingFex.Api.Finances.Infrastructure.Pdf.SimpleReceiptPdfRenderer>();
builder.Services.AddScoped<BuildingFex.Api.Finances.Application.Internal.ReceiptDocuments.IReceiptDocumentService,
    BuildingFex.Api.Finances.Application.Internal.ReceiptDocuments.ReceiptDocumentService>();
// RF021 / RNF011: facturación mensual automática (idempotente, diaria).
// En Lambda lo hace el job correspondiente ("monthly-billing").
if (!LambdaHosting.IsLambda)
    builder.Services.AddHostedService<BuildingFex.Api.Finances.Application.Internal.Billing.MonthlyBillingWorker>();
builder.Services.Configure<MercadoPagoSettings>(builder.Configuration.GetSection("MercadoPago"));
builder.Services.AddScoped<IMercadoPagoService, MercadoPagoService>();
builder.Services.AddScoped<DbJsonFinanceSeeder>();

// SocialSpaces
builder.Services.AddScoped<ISocialSpaceRepository, SocialSpaceRepository>();
builder.Services.AddScoped<IReservationRepository, ReservationRepository>();
builder.Services.AddScoped<SocialSpacesOwnerResolver>();
builder.Services.AddScoped<DbJsonSocialSpacesSeeder>();
builder.Services.AddSingleton<BuildingFex.Api.SocialSpaces.Domain.Services.ReservationPolicy>();
builder.Services.AddSingleton<SocialSpacesClock>();
builder.Services.AddScoped<BuildingFex.Api.SocialSpaces.Application.Internal.CommandServices.ReservationCommandService>();
builder.Services.AddScoped<BuildingFex.Api.Finances.Application.Internal.Delinquency.IAccountStatusQueryService,
    BuildingFex.Api.Finances.Application.Internal.Delinquency.AccountStatusQueryService>();
builder.Services.AddScoped<BuildingFex.Api.SocialSpaces.Application.Internal.OutboundServices.IAccountStatusAcl,
    BuildingFex.Api.SocialSpaces.Infrastructure.Acl.FinancesAccountStatusAcl>();

// Communications (announcements)
builder.Services.AddScoped<IAnnouncementRepository, AnnouncementRepository>();
builder.Services.AddScoped<IAnnouncementCommandService, AnnouncementCommandService>();
builder.Services.AddScoped<IAnnouncementQueryService, AnnouncementQueryService>();
builder.Services.AddScoped<DbJsonAnnouncementSeeder>();

// Team (staff, maintenance tasks, shifts)
builder.Services.AddScoped<BuildingFex.Api.Team.Domain.Repositories.ITeamWorkerRepository,
    BuildingFex.Api.Team.Infrastructure.Persistence.EntityFrameworkCore.Repositories.TeamWorkerRepository>();
builder.Services.AddScoped<BuildingFex.Api.Team.Domain.Repositories.IMaintenanceTaskRepository,
    BuildingFex.Api.Team.Infrastructure.Persistence.EntityFrameworkCore.Repositories.MaintenanceTaskRepository>();
builder.Services.AddScoped<BuildingFex.Api.Team.Domain.Repositories.IShiftRepository,
    BuildingFex.Api.Team.Infrastructure.Persistence.EntityFrameworkCore.Repositories.ShiftRepository>();
builder.Services.AddSingleton<BuildingFex.Api.Team.Domain.Services.WorkloadPolicy>();
builder.Services.AddScoped<BuildingFex.Api.Team.Application.Internal.CommandServices.TeamWorkerCommandService>();
builder.Services.AddScoped<BuildingFex.Api.Team.Application.Internal.CommandServices.MaintenanceTaskCommandService>();
builder.Services.AddScoped<BuildingFex.Api.Team.Application.Internal.CommandServices.ShiftCommandService>();

var app = builder.Build();

if (LambdaHosting.IsJobsFunction)
{
    await LambdaJobs.RunAsync(app);
    return;
}

// En Lambda el esquema lo aplica el job "migrate" una vez por despliegue, no en cada arranque en frío.
if (!LambdaHosting.IsLambda)
    await DatabaseBootstrap.RunAsync(app.Services, builder.Configuration, app.Environment.ContentRootPath);

if (app.Environment.IsDevelopment() || RailwayHosting.IsRailwayDeployment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Rate limiting: X-Forwarded-For original (antes de que UseForwardedHeaders lo reescriba).
app.UseClientIpCapture();
app.UseForwardedHeaders();
app.UseCors("AllowFrontend");
app.UseRateLimiter();
// Railway y API Gateway (Lambda) terminan TLS antes de llegar a la app.
if (!app.Environment.IsDevelopment() && !RailwayHosting.IsRailwayDeployment() && !LambdaHosting.IsLambda)
    app.UseHttpsRedirection();
app.UseAuthentication();
// IAM: cuentas DEACTIVATED → 403 ACCOUNT_DEACTIVATED en todo request autenticado.
app.UseAccountStatusGuard();
app.UseBoardReadOnlyGuard();
app.UseAuthorization();
app.MapControllers();
// API Gateway HTTP API no mantiene conexiones de SignalR: en Lambda el front usa polling.
if (!LambdaHosting.IsLambda)
    app.MapHub<BuildingFex.Api.ResidentCare.Interfaces.Hubs.SupportHub>("/hubs/support");

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "buildingfex-api" }))
    .WithTags("Health");

app.Run();
