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
    private const string HeaderName = ApiKeyValidator.HeaderName;
    private const string ConfigKey = ApiKeyValidator.ConfigKey;

    /// <summary>
    /// Liveness probes, which cannot carry a secret.
    ///
    /// <para>
    /// This middleware guards a WHOLE host, so it answered 401 to the container's own
    /// health check — and to every orchestrator that polls one. The symptom was a registry
    /// permanently <c>unhealthy</c>, a stream of <c>API key missing on GET /health</c> warnings
    /// hiding real ones, and Compose refusing to start whatever depended on it. Exempting the
    /// two probe paths leaks nothing the API does not already serve anonymously from its own
    /// <c>/health</c>: a status word and a timestamp, no component detail.
    /// </para>
    /// </summary>
    private static readonly string[] ProbePaths = ["/health", "/alive"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (ProbePaths.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

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

        // Constant-time: an ordinary comparison leaks the shared prefix length through timing.
        if (!ApiKeyValidator.Matches(expectedKey, providedKey))
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
