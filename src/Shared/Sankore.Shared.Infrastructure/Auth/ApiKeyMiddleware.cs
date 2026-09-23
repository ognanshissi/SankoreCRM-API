namespace Sankore.Shared.Infrastructure.Auth;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Validates the X-Api-Key header on internal/service-to-service routes.
/// Register with <see cref="ApiKeyMiddlewareExtensions.UseApiKeyAuth"/> on routes
/// that should be protected by an API key (e.g. tenant registry endpoints).
/// </summary>
public sealed class ApiKeyMiddleware(
    RequestDelegate next,
    IConfiguration config,
    ILogger<ApiKeyMiddleware> logger)
{
    private const string HeaderName = "X-Api-Key";
    private const string ConfigKey = "ApiKey";

    public async Task InvokeAsync(HttpContext context)
    {
        var expectedKey = config[ConfigKey];

        // If no API key is configured, skip validation (dev mode)
        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var providedKey)
            || string.IsNullOrWhiteSpace(providedKey))
        {
            logger.LogWarning("API key missing on {Method} {Path}",
                context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "API key is required." });
            return;
        }

        if (!string.Equals(expectedKey, providedKey, StringComparison.Ordinal))
        {
            logger.LogWarning("Invalid API key on {Method} {Path}",
                context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API key." });
            return;
        }

        await next(context);
    }
}

public static class ApiKeyMiddlewareExtensions
{
    /// <summary>
    /// Adds API key validation middleware to the pipeline.
    /// Validates the X-Api-Key header against the "ApiKey" configuration value.
    /// If no ApiKey is configured, validation is skipped (dev mode).
    /// </summary>
    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app)
        => app.UseMiddleware<ApiKeyMiddleware>();
}
