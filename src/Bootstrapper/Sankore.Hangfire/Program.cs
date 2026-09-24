using Hangfire;
using Hangfire.PostgreSql;
using Sankore.Hangfire.Infrastructure;

// ---------------------------------------------------------------------
// Sankore.Hangfire — the background job dashboard, and nothing else.
//
// This host deliberately does NOT call AddHangfireServer(): Sankore.Api owns job
// execution and recurring job registration. Here we only read and command the
// same Hangfire Postgres storage, so visualising or pausing jobs can never
// compete with the API for work.
// ---------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Database is required — it must point at the same database as Sankore.Api.");

// Storage configuration must match Sankore.Api exactly, or serialized jobs
// written by one host cannot be read by the other.
builder.Services.AddHangfire(cfg =>
    cfg.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
       .UseSimpleAssemblyNameTypeSerializer()
       .UseRecommendedSerializerSettings()
       .UsePostgreSqlStorage(opts =>
           opts.UseNpgsqlConnection(connectionString)));

var app = builder.Build();

app.MapDefaultEndpoints();

SankoreDashboard.Register();

var dashboardLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger<BasicAuthDashboardFilter>();

var username = app.Configuration["Hangfire:Dashboard:Username"];
var password = app.Configuration["Hangfire:Dashboard:Password"];

if (string.IsNullOrWhiteSpace(password))
{
    app.Logger.LogError(
        "Hangfire:Dashboard:Password is not set. The dashboard will reject every request. "
        + "Set it with user-secrets locally, or Hangfire__Dashboard__Password in the environment.");
}

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "Sankore CRM — Jobs",
    Authorization = [new BasicAuthDashboardFilter(username, password, dashboardLogger)],
    // Deleting, requeueing and pausing is the whole point of this host.
    IsReadOnlyFunc = _ => false,
    DisplayStorageConnectionString = false,
    DarkModeEnabled = true,
});

app.MapGet("/", () => Results.Redirect("/hangfire")).ExcludeFromDescription();

app.Run();
