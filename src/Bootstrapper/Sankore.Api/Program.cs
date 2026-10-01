using System.Linq.Expressions;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Sankore.Api.Features.Audit.GetAuditEntries;
using Sankore.Api.Features.Bootstrap;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Api.Infrastructure;
using Sankore.Api.Infrastructure.Audit;
using Sankore.Modules.Leads;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Administration;
using Sankore.Modules.Workflow;
using Sankore.Modules.Notifications;
using Sankore.Modules.Customers;
using Sankore.Modules.Kyc;
using Sankore.Shared.ObjectStorage;
using Sankore.Api.Features.ObjectStorage;
using Sankore.Api.Features.ObjectStorage.MigrateObjects;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Notifications.Infrastructure.Consumers;
using Sankore.Modules.Workflow.Infrastructure.Consumers;
using Sankore.Modules.Workflow.Infrastructure.Triggers;
using Hangfire;
using Hangfire.PostgreSql;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.DataProtection;
using Sankore.Shared.Infrastructure.Logging;
using Sankore.Shared.Infrastructure.Localization;
using Sankore.Shared.Infrastructure.Secrets;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

// Emitting the OpenAPI document is a build-time concern, not a run. The front-end client in
// SankoreFront is generated from swaggers/sankore-crm-api-swagger.json, and producing that file
// used to mean booting the whole API — which means a reachable, already-migrated PostgreSQL, for
// a document that describes routes and schemas and depends on no row anywhere. With this flag the
// host builds, maps its endpoints, writes the document and exits:
//
//   dotnet run --project src/Bootstrapper/Sankore.Api -- --emit-openapi <path>
//
// Nothing is migrated and nothing is seeded in that mode, deliberately: a developer regenerating
// a client must not be able to alter a database by doing so.
var emitOpenApiTo = OpenApiEmitter.OutputPathFrom(args);

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Serialize enums as their string names in all HTTP JSON responses and requests.
// This keeps the wire format human-readable and consistent with the Swagger schema.
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.GuidConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.NullableGuidConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.TimeSpanConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.DateOnlyConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.NullableDateOnlyConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.SourceSettingsJsonConverter());
});
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();

// Origins from Cors:AllowedOrigins — see CorsSetup for why they are not literals here.
builder.Services.AddSankoreCors(builder.Configuration, builder.Environment);

builder.AddSeqEndpoint("seq");
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration;
    o.CombineLogs = true;
});

// ---------------------------------------------------------------------
// 1. Cross-cutting infrastructure (auth, MediatR pipeline, messaging bus)
// ---------------------------------------------------------------------

builder.Services.AddHttpContextAccessor();

// Context-aware registrations: background jobs (Hangfire) set BackgroundJobContext
// via AsyncLocal; HTTP requests fall back to the standard JWT-based implementations.
builder.Services.AddScoped<ICurrentUser>(sp =>
{
    if (BackgroundJobContext.CurrentUser is { } bgUser)
        return bgUser;
    return new HttpContextCurrentUser(sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>());
});
builder.Services.AddScoped<ITenantContext>(sp =>
{
    if (BackgroundJobContext.CurrentTenant is { } bgTenant)
        return bgTenant;
    return new HttpTenantContext(sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>());
});
builder.Services.AddTenantStore(builder.Configuration);
builder.Services.AddLanguageResolution();
builder.Services.AddLocalization(opts => opts.ResourcesPath = "Resources");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtConfig = builder.Configuration.GetSection("Jwt");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtConfig["Issuer"],
            ValidAudience = jwtConfig["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtConfig["SigningKey"]!))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSankoreAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict limit for unauthenticated auth endpoints (login, forgot-password, etc.)
    // 5 requests per minute per IP address — mitigates brute-force and enumeration attacks.
    options.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 5,
                QueueLimit = 0,
            }));

    // Standard limit for all authenticated API endpoints.
    // 200 requests per minute keyed by user ID (JWT sub); falls back to IP for anonymous calls.
    options.AddPolicy("api", ctx =>
    {
        var userId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var key = userId ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            PermitLimit = 200,
            QueueLimit = 0,
        });
    });

    // Web ingest: 10 req/min per IP+key (US-F13.37-BE-12)
    options.AddPolicy("ingest-key", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = ctx.Request.RouteValues["publicKey"]?.ToString() ?? "none";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"ingest:{ip}:{key}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 10,
                QueueLimit = 0,
            });
    });
});

// MediatR — register Bootstrapper's own handlers (audit query, etc.)
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// MediatR pipeline behaviors: order matters (outermost first).
// Logging -> Validation -> Transaction -> Audit -> [Handler]
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
// Agency perimeter: rejects any IAgencyScopedRequest whose TargetAgencyId falls outside the
// caller's perimeter BEFORE a transaction is opened. Inert for every other request.
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AgencyAuthorizationBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));

// Audit — dedicated DbContext + real writer.
// AddDbContextFactory registers both the factory (singleton) and a scoped
// DbContext so that EF tooling and the migration startup code can resolve it.
var connectionString = builder.Configuration.GetConnectionString("Database")!;
builder.Services.AddDbContextFactory<AuditDbContext>(opts =>
    opts.UseNpgsql(connectionString,
        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "audit"))
        .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IAuditWriter, SqlAuditWriter>();

// Data Protection key ring — PostgreSQL-backed, fixed application name.
// Identity signs every account-activation and password-reset token with this ring. The framework
// default keeps it in the container filesystem, so a redeploy silently invalidated every
// activation link already in a user's inbox. See SankoreDataProtection.
builder.Services.AddSankoreDataProtection(
    builder.Configuration,
    opts => opts.UseNpgsql(connectionString,
            b => b.MigrationsHistoryTable("__EFMigrationsHistory", "dataprotection"))
        .UseSnakeCaseNamingConvention());

// Secrets vault — AES-256-GCM encrypted, PostgreSQL-backed.
// AddSecretsVault validates Secrets:EncryptionKey at start-up: the app refuses to boot without
// a usable key rather than throwing on the first write.
builder.Services.AddSecretsVault(
    builder.Configuration,
    opts => opts.UseNpgsql(connectionString,
            b => b.MigrationsHistoryTable("__EFMigrationsHistory", "secrets"))
        .UseSnakeCaseNamingConvention());

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<Sankore.Modules.Kyc.Features.Limits.Consumers.KycTierChangedCacheConsumer>();

    x.AddConsumer<Sankore.Modules.Kyc.Features.Files.Consumers.ClientCreatedKycConsumer>();
    x.AddConsumer<Sankore.Modules.Kyc.Features.Files.Consumers.KycRequestedConsumer>();

    x.AddConsumer<Sankore.Modules.Leads.Features.Consumers.LeadAutoDispatchConsumer>();
    x.AddConsumer<LeadDispatchedTaskConsumer>();
    x.AddConsumer<LeadDispatchingFailedTaskConsumer>();

    // Notification module consumers
    x.AddConsumer<TenantNotificationSettingsChangedConsumer>();

    // Workflow module consumers
    x.AddConsumer<WorkflowTriggerConsumer>();
    x.AddConsumer<ChildWorkflowCompletedConsumer>();

    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.KycValidatedConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.KycRejectedConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.KycRiskLevelChangedConsumer>();
    // Development-only auto-validation stub; inert outside Development (US-M01-BE-13).
    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.DevKycAutoValidateConsumer>();

    // 360° timeline projection (US-M01-BE-26). Adding a source module later means
    // adding a consumer here — the timeline model itself never changes.
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientCreatedTimelineConsumer>();
    // Welcomes a prospect who just became a client (lead conversions only).
    x.AddConsumer<Sankore.Modules.Customers.Features.LeadConversion.Consumers.ClientWelcomeEmailConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientActivatedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientSuspendedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientArchivedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientTransferredTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientsMergedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientSegmentChangedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.GroupMembershipChangedTimelineConsumer>();

    var useRabbitMq = builder.Configuration.GetValue<bool>("Messaging:UseRabbitMq");

    if (useRabbitMq)
    {
        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(builder.Configuration["Messaging:RabbitMqHost"] ?? "localhost");
        });
    }
    else
    {
        x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
    }
});

// Hangfire — background job processing (lead import, etc.)
builder.Services.AddHangfire(cfg =>
    cfg.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
       .UseSimpleAssemblyNameTypeSerializer()
       .UseRecommendedSerializerSettings()
       .UsePostgreSqlStorage(opts =>
           opts.UseNpgsqlConnection(connectionString)));
// Not in emit mode: the document describes routes, and the one thing that would still reach for
// PostgreSQL while writing it is this worker starting up.
if (emitOpenApiTo is null) builder.Services.AddHangfireServer();

// Platform job: the one-shot copy of stored objects to the bucket. Transient like every module's
// job type — Hangfire activates it from the container.
builder.Services.AddTransient<MigrateObjectsJob>();

// Redis distributed cache — used by Notifications module for provider resolution
builder.AddRedisDistributedCache("redis");

builder.Services.AddEndpointsApiExplorer();

#region Swagger Generation
builder.Services.AddSwaggerGen(options => 
{
    options.AddSecurityDefinition("BearerToken", new ()
    {
        Name = "Authorization",
        Description = "Token-based authentication using the Bearer scheme",
        Type = SecuritySchemeType.Http,
        In = ParameterLocation.Header,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new()
    {
        {
            new()
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "BearerToken" }
            },
            new List<string>()
        }
    });

    // Render every enum as a string schema with all valid member names listed.
    // Ensures Swagger UI shows "New | Open | Qualifying | ..." instead of "0 | 1 | 2 | ...".
    options.SchemaFilter<EnumSchemaFilter>();
    options.UseInlineDefinitionsForEnums();
});
#endregion

foreach (var concern in ObjectStorageConcerns.All)
{
    builder.Services.AddObjectBackend(
        builder.Configuration,
        concern.Name,
        concern.ResolveLocalRoot(builder.Configuration, builder.Environment));
}

builder.Services.AddAdministrationModule(builder.Configuration);
builder.Services.AddLeadsModule(builder.Configuration);
builder.Services.AddWorkflowModule(builder.Configuration);
builder.Services.AddNotificationsModule(builder.Configuration);
builder.Services.AddCustomersModule(builder.Configuration);      // M01

builder.Services.AddKycModule(builder.Configuration);            // M02

if (emitOpenApiTo is not null)
{
    builder.Services.RemoveAll<IHostedService>();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}

var app = builder.Build();

app.MapDefaultEndpoints();
app.LogCorsOrigins();

// ---------------------------------------------------------------------
// 3. Ensure database schemas exist (creates tables when no migrations are applied yet)
// ---------------------------------------------------------------------

if (emitOpenApiTo is null)
{
    using var scope = app.Services.CreateScope();

    // Audit schema — independent of all module schemas.
    var auditDb = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
    await auditDb.Database.MigrateAsync();
    await SankoreDataProtection.InitializeAsync(scope.ServiceProvider);

    // Secrets vault — shared infrastructure, so no module owns its schema: it has to be
    await SecretsServiceCollectionExtensions.InitializeAsync(scope.ServiceProvider);

    // AdministrationModule.InitializeAsync runs migrations AND seeds system roles.
    await LeadsModule.InitializeAsync(scope.ServiceProvider);
    await AdministrationModule.InitializeAsync(scope.ServiceProvider);
    await WorkflowModule.InitializeAsync(scope.ServiceProvider);
    await NotificationsModule.InitializeAsync(scope.ServiceProvider);
    await CustomersModule.InitializeAsync(scope.ServiceProvider);
    await KycModule.InitializeAsync(scope.ServiceProvider);
}

// ---------------------------------------------------------------------
// 4. HTTP pipeline
// ---------------------------------------------------------------------

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (emitOpenApiTo is null) { 
    try {
        var tenantStore = app.Services.GetRequiredService<Sankore.Shared.Kernel.ITenantStore>();
        var activeTenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var pauseStore = new RecurringJobPauseStore(
            app.Services.GetRequiredService<Hangfire.JobStorage>(),
            app.Services.GetRequiredService<Hangfire.IRecurringJobManager>());

        var pausedJobIds = pauseStore.GetPausedIds();
        var skipped = new List<string>();

        void AddOrUpdate<TJob>(string jobId, Expression<Func<TJob, Task>> methodCall, string cron)
        {
            if (pausedJobIds.Contains(jobId))
            {
                skipped.Add(jobId);
                return;
            }

            Hangfire.RecurringJob.AddOrUpdate(jobId, methodCall, cron);
        }

        foreach (var t in activeTenants)
    {
        var tenantId = t.Id;
        var suffix = tenantId.ToString()[..8];

        AddOrUpdate<Sankore.Modules.Leads.Features.SlaMonitoring.CheckSlaBreachesJob>(
            $"sla-breach-check-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "*/15 * * * *");

        AddOrUpdate<Sankore.Modules.Leads.Features.NurturingExecution.ExecuteNurturingJob>(
            $"nurturing-execution-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "*/10 * * * *");

        AddOrUpdate<Sankore.Modules.Leads.Features.ReactivateRecycledLeads.ReactivateRecycledLeadsJob>(
            $"reactivate-recycled-leads-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "0 3 * * *");
    }

        // Pull orchestrator — runs every minute across all tenants (F13.37-BE-21)
        AddOrUpdate<Sankore.Modules.Leads.Features.Ingestion.Pull.LeadSourcePullOrchestratorJob>(
            "lead-source-pull-orchestrator",
            job => job.ExecuteAsync(),
            "* * * * *");

        // ── Customers module (M01) ────────────────────────────────────────────────
        // All four are GLOBAL orchestrators: they iterate active tenants themselves and
        // enqueue one opaque per-tenant job each, so adding a tenant needs no new
        // recurring registration and the job arguments stay opaque identifiers.
        AddOrUpdate<Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates.DetectDuplicatesOrchestratorJob>(
            "customers-detect-duplicates-orchestrator",
            job => job.ExecuteAsync(),
            "0 2 * * *");                                    // nightly (US-M01-BE-24)

        AddOrUpdate<Sankore.Modules.Customers.Features.Timeline.Segments.AssignSegmentsOrchestratorJob>(
            "customers-assign-segments-orchestrator",
            job => job.ExecuteAsync(),
            "0 3 * * *");                                    // nightly (US-M01-BE-27)

        AddOrUpdate<Sankore.Modules.Customers.Features.Timeline.Loyalty.ComputeLoyaltyScoresOrchestratorJob>(
            "customers-loyalty-scores-orchestrator",
            job => job.ExecuteAsync(),
            "30 3 * * *");                                   // nightly (US-M01-BE-28)

        // ── KYC module (M02) ──────────────────────────────────────────────────────
        // GLOBAL like M01's: it walks the active tenants itself and enqueues one opaque per-tenant
        // job, so a new tenant needs no new recurring registration. 01:00 is before M01's 02:00 and
        // 03:00 sweeps — a review that downgrades a tier should land before the nightly scoring reads
        // it.
        AddOrUpdate<Sankore.Modules.Kyc.Features.Reviews.KycReviewOrchestratorJob>(
            "kyc-review-orchestrator",
            job => job.ExecuteAsync(),
            "0 1 * * *");                                    // daily (KYC-B-07)

        AddOrUpdate<Sankore.Modules.Customers.Features.Compliance.Retention.IdentifyRetentionCandidatesOrchestratorJob>(
            "customers-retention-candidates-orchestrator",
            job => job.ExecuteAsync(),
            "0 4 1 * *");                                    // monthly (US-M01-BE-29)

        app.Logger.LogInformation(
            "Registered recurring Hangfire jobs for {TenantCount} active tenant(s), plus the global pull orchestrator.",
            activeTenants.Count);

        if (skipped.Count > 0) 
        {
            app.Logger.LogInformation(
                "Skipped {SkippedCount} recurring job(s) paused from the Hangfire dashboard: {SkippedJobIds}. "
                + "They will not run until resumed there.",
                skipped.Count,
                string.Join(", ", skipped)); 
        } 
    } 
    catch (Exception ex)
    {
        app.Logger.LogError(ex,
            "Failed to register recurring Hangfire jobs. The API will start WITHOUT them, so "
            + "SLA escalation, nurturing, lead recycling and scheduled pulls will not run on this "
            + "instance. This usually means another instance is already running against the same database."); 
    }
}

app.UseExceptionHandler();
app.UseHttpLogging();
app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseTenantResolution();   // extract + verify tenant against external store
app.UseRequestLocalization(opts =>
{
    opts.SupportedCultures = [new("fr"), new("en")];
    opts.SupportedUICultures = [new("fr"), new("en")];
    opts.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("fr");
    opts.ApplyCurrentCultureToResponseHeaders = true;
});
app.UseLanguageResolution(); // resolve active language (JWT claim → Accept-Language → default fr) and set CurrentUICulture
app.UseCorrelationId();      // push CorrelationId + TenantId + UserId into log scope
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
    .WithTags("Health")
    .WithOpenApi()
    .AllowAnonymous();

// V1
var appVersion1 = app.MapGroup("api/v1").RequireRateLimiting("api");

appVersion1.MapAdministrationModuleEndpoints();
appVersion1.MapLeadsEndpoints();
appVersion1.MapWorkflowModuleEndpoints();
appVersion1.MapNotificationsModuleEndpoints();
appVersion1.MapCustomersModuleEndpoints();
appVersion1.MapKycModuleEndpoints();

appVersion1.MapGroup("audit").MapGetAuditEntries();
appVersion1.MapGroup("object-storage").MapMigrateObjects();

app.MapBootstrapEndpoints();

// Public (unauthenticated) ingest endpoints — outside api/v1, own rate limiting
app.MapPublicIngestEndpoints();

// app.MapKycEndpoints();
// app.MapLoansEndpoints();

// After every Map* call: the document is built from the endpoint data sources, so a route mapped
// below this line would be missing from the generated client without anything failing.
if (emitOpenApiTo is not null)
{
    await OpenApiEmitter.WriteAsync(app, emitOpenApiTo);
    return;
}

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
