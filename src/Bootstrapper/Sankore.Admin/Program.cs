using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Auth;
using Sankore.Admin.Features.Modules;
using Sankore.Admin.Features.Support;
using Sankore.Admin.Features.TenantDomains;
using Sankore.Admin.Features.Tenants;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;
using Sankore.Admin.Infrastructure.OpenApi;
using Sankore.Shared.Infrastructure.Auth;

// Build-time only: write the OpenAPI document for the admin front-end's client and exit. Nothing
// is migrated or seeded in that mode, and the signing key is not required.
var emitOpenApiTo = OpenApiEmitter.OutputPathFrom(args);

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddDbContext<AdminDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("AdminDatabase")));

// The tenant TenantSeeder provisions at start-up. No ValidateOnStart: a malformed value is
// reported by the seeder and the registry still comes up, so the manual POST /api/v1/tenants
// stays reachable.
builder.Services.Configure<DefaultTenantOptions>(
    builder.Configuration.GetSection(DefaultTenantOptions.Section));
builder.Services.Configure<PlatformAdminSeedOptions>(
    builder.Configuration.GetSection(PlatformAdminSeedOptions.Section));

// Hourly closing of the support tickets left Resolved for a week. Not in --emit-openapi mode:
// the emitter starts the host, and the service would reach for a database that is not there.
builder.Services.AddSingleton(TimeProvider.System);
if (emitOpenApiTo is null)
    builder.Services.AddHostedService<SupportAutoCloseService>();

builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// Enums as names, like Sankore.Api. This host used to send them as integers, harmless while no
// response carried one; the admin front-end's generated client is configured for string enums.
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

builder.Services.AddAdminAuthentication(builder.Configuration, validateOnStart: emitOpenApiTo is null);
using (var loggerFactory = LoggerFactory.Create(b => b.AddConsole()))
{
    builder.Services.AddAdminCors(
        builder.Configuration, builder.Environment, loggerFactory.CreateLogger("Sankore.Admin.Cors"));
}

builder.Services.AddOpenApi();
builder.Services.AddAdminSwagger();

var app = builder.Build();

app.MapDefaultEndpoints();

if (emitOpenApiTo is null)
{
    using var scope = app.Services.CreateScope();
    var adminDbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
    await adminDbContext.Database.MigrateAsync();

    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

    await TenantSeeder.SeedAsync(
        adminDbContext,
        scope.ServiceProvider.GetRequiredService<IOptions<DefaultTenantOptions>>().Value,
        app.Environment,
        loggerFactory.CreateLogger(nameof(TenantSeeder)));

    await PlatformAdminSeeder.SeedAsync(
        adminDbContext,
        scope.ServiceProvider.GetRequiredService<IOptions<PlatformAdminSeedOptions>>().Value,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<PlatformAdmin>>(),
        loggerFactory.CreateLogger(nameof(PlatformAdminSeeder)));
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

// Before the API key: a browser's CORS preflight carries no credential at all, and answering it
// 401 is reported by the browser as a CORS failure, not as an authentication one.
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// The registry accepts EITHER credential: the service key Sankore.Api sends, or a platform
// administrator's JWT from the admin front-end. The key must never be shipped to a browser —
// whoever holds it administers every tenant — so an operator proves themselves with a token,
// and a request that carries a valid one skips the key check. "Valid" is AdminClaims.IsPlatformAdmin:
// authenticated by the AdminJwt bearer scheme AND carrying the operator scope — not bare
// authentication, which would silently widen the day another scheme is added to this host. A
// tenant user's token is signed with another key and never authenticates here at all.
//
// The sign-in routes skip the key unconditionally: they are what produces the token.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments(AuthEndpoints.Prefix, StringComparison.OrdinalIgnoreCase)
           && !AdminClaims.IsPlatformAdmin(ctx.User),
    branch => branch.UseApiKeyAuth());

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

app.MapAuthEndpoints();
app.MapTenantEndpoints();
app.MapTenantDomainEndpoints();
app.MapModuleEndpoints();
app.MapSupportEndpoints();

if (emitOpenApiTo is not null)
{
    await OpenApiEmitter.WriteAsync(app, emitOpenApiTo);
    return;
}

app.Run();
