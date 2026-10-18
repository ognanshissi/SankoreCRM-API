using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Sankore.Admin.Features.TenantDomains;
using Sankore.Admin.Features.Tenants;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddDbContext<AdminDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("AdminDatabase")));

// The tenant TenantSeeder provisions at start-up. No ValidateOnStart: a malformed value is
// reported by the seeder and the registry still comes up, so the manual POST /api/v1/tenants
// stays reachable.
builder.Services.Configure<DefaultTenantOptions>(
    builder.Configuration.GetSection(DefaultTenantOptions.Section));

builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.CustomSchemaIds(type => type.FullName);

    options.SwaggerDoc("v1", new()
    {
        Title = "Sankore Admin API",
        Version = "v1",
        Description = "Administration API for SankoreCRM"
    });

    options.AddSecurityDefinition("Bearer", new()
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token."
    });

    options.AddSecurityRequirement(new()
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            []
        }
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

// ---------------------------------------------------------------------
//  Ensure database schemas exist (creates tables when no migrations are applied yet)
// ---------------------------------------------------------------------

using (var scope = app.Services.CreateScope())
{
    var adminDbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
    await adminDbContext.Database.MigrateAsync();

    await TenantSeeder.SeedAsync(
        adminDbContext,
        scope.ServiceProvider.GetRequiredService<IOptions<DefaultTenantOptions>>().Value,
        app.Environment,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(TenantSeeder)));
}

// Served only where SwaggerExposure allows (Development by default) and always behind HTTP Basic
// auth. The gate must come first: UseSwagger answers the request itself. Note that UseApiKeyAuth
// below never protected these routes — it is registered after them.
if (SwaggerExposure.IsEnabled(app.Configuration, app.Environment))
{
    app.UseSwaggerBasicAuth();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Sankore Admin API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();

// API key validation for service-to-service calls from SankoreApi
app.UseApiKeyAuth();

// Same flat liveness route the API declares, and for the same reason: `MapDefaultEndpoints`
// (ServiceDefaults) maps /health and /alive ONLY in Development, so outside it this host answered
// 404 to every probe — and 401 before that, because UseApiKeyAuth above guards the whole host
// and a container health check cannot carry a secret. A registry that cannot be probed is one
// Compose reports `unhealthy` for ever, and whatever depends on it never starts.
//
// It reports liveness and nothing else: reaching this point means the migrations and the default
// tenant seed already ran, since both happen before app.Run(). The anonymous access is the same
// trade the API makes — a status word and a timestamp, no component detail.
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
    .WithTags("Health")
    .WithOpenApi()
    .AllowAnonymous();

app.MapTenantEndpoints();
app.MapTenantDomainEndpoints();

app.Run();
