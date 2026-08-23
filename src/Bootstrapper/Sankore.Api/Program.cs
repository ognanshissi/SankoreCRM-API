using System.Text;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Sankore.Api.Features.Audit.GetAuditEntries;
using Sankore.Api.Infrastructure;
using Sankore.Api.Infrastructure.Audit;
using Sankore.Modules.Leads;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Administration;
using Sankore.Modules.Workflow;
using Sankore.Modules.Notifications;
using Sankore.Modules.Notifications.Infrastructure.Consumers;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Logging;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();


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
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddTenantStore(builder.Configuration);

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

// MediatR — register Bootstrapper's own handlers (audit query, etc.)
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// MediatR pipeline behaviors: order matters (outermost first).
// Logging -> Validation -> Transaction -> Audit -> [Handler]
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));

// Audit — dedicated DbContext + real writer.
// AddDbContextFactory registers both the factory (singleton) and a scoped
// DbContext so that EF tooling and the migration startup code can resolve it.
var connectionString = builder.Configuration.GetConnectionString("Database")!;
builder.Services.AddDbContextFactory<AuditDbContext>(opts =>
    opts.UseNpgsql(connectionString,
        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "audit")));

builder.Services.AddScoped<IAuditWriter, SqlAuditWriter>();

// Message bus (MassTransit). In-memory transport by default for local dev;
// swap to RabbitMQ/Kafka via configuration for staging/production without
// touching module code (OutboxProcessor<T> only depends on IBus).
builder.Services.AddMassTransit(x =>
{
    // Notification module consumers
    x.AddConsumer<TenantNotificationSettingsChangedConsumer>();

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

// Redis distributed cache — used by Notifications module for provider resolution
builder.AddRedisDistributedCache("redis");

builder.Services.AddEndpointsApiExplorer();

#region Swagger Generation
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<Sankore.Api.Infrastructure.TenantHeaderOperationFilter>();

    options.AddSecurityDefinition("BearerToken", new ()
    {
        Name = "Authorization",
        Description = "Token-based authentication using the Bearer scheme",
        Type = SecuritySchemeType.Http,
        In = ParameterLocation.Header,
        Scheme = "Bearer"
    });

    options.AddSecurityDefinition("TenantHeader", new()
    {
        Name = "x-tenant-id",
        Description = "Tenant identifier (UUID). Required when no JWT is present or to override the JWT tenant claim.",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
    });

    options.AddSecurityRequirement(new()
    {
        {
            new()
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "BearerToken" }
            },
            new List<string>()
        },
        {
            new()
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "TenantHeader" }
            },
            new List<string>()
        }
    });
});
#endregion


// ---------------------------------------------------------------------
// 2. Module registration — one line per module, each module owns its own
//    DbContext, handlers, validators, and endpoints internally.
// ---------------------------------------------------------------------

builder.Services.AddAdministrationModule(builder.Configuration);
builder.Services.AddLeadsModule(builder.Configuration);
builder.Services.AddWorkflowModule(builder.Configuration);
builder.Services.AddNotificationsModule(builder.Configuration);
// builder.Services.AddCustomersModule(builder.Configuration);   // M01 — same pattern
// builder.Services.AddKycModule(builder.Configuration);         // M02 — same pattern
// builder.Services.AddLoansModule(builder.Configuration);       // M04 — same pattern

var app = builder.Build();

app.MapDefaultEndpoints();

// ---------------------------------------------------------------------
// 3. Ensure database schemas exist (creates tables when no migrations are applied yet)
// ---------------------------------------------------------------------

using (var scope = app.Services.CreateScope())
{
    // Audit schema — independent of all module schemas.
    var auditDb = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
    await auditDb.Database.MigrateAsync();

    // Each module owns its own migration + initialization.
    // AdministrationModule.InitializeAsync runs migrations AND seeds system roles.
    var leadsDb = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
    await leadsDb.Database.MigrateAsync();
    await AdministrationModule.InitializeAsync(scope.ServiceProvider);
    await WorkflowModule.InitializeAsync(scope.ServiceProvider);
    await NotificationsModule.InitializeAsync(scope.ServiceProvider);
}

// ---------------------------------------------------------------------
// 4. HTTP pipeline
// ---------------------------------------------------------------------

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpLogging();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseTenantResolution();   // extract + verify tenant against external store
app.UseCorrelationId();      // push CorrelationId + TenantId + UserId into log scope
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
    .WithTags("Health")
    .WithOpenApi()
    .AllowAnonymous();

// V1
var appVersion1 = app.MapGroup("api/v1");

appVersion1.MapAdministrationModuleEndpoints();
appVersion1.MapLeadsEndpoints();
appVersion1.MapWorkflowModuleEndpoints();
appVersion1.MapNotificationsModuleEndpoints();

appVersion1.MapGroup("audit").MapGetAuditEntries();

// app.MapCustomersEndpoints();
// app.MapKycEndpoints();
// app.MapLoansEndpoints();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
