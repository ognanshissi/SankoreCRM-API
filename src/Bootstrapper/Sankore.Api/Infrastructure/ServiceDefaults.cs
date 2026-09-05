using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Sankore.Api.Infrastructure;

/// <summary>
/// Service defaults for Aspire integration.
/// </summary>
public static class ServiceDefaults
{
    /// <summary>
    /// Adds default Aspire service configuration.
    /// </summary>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks();
        return builder;
    }

    /// <summary>
    /// Maps default Aspire endpoints.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();

        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks("/health", new HealthCheckOptions
            {
                ResponseWriter = async (ctx, report) =>
                {
                    var result = System.Text.Json.JsonSerializer.Serialize(
                        new
                        {
                            status = report.Status.ToString(),
                            checks = report.Entries.Select(e => new
                            {
                                name = e.Key,
                                status = e.Value.Status.ToString(),
                                description = e.Value.Description,
                                duration = e.Value.Duration.TotalMilliseconds
                            })
                        });

                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(result);
                }
            });
        }

        return app;
    }
}
