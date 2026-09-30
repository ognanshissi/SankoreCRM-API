namespace Sankore.Shared.Infrastructure.Auth;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Requires a valid <c>X-Api-Key</c> on a single endpoint, for operations that cannot be behind
/// a JWT because they are what creates the first account.
///
/// It differs from <see cref="ApiKeyMiddleware"/> in one deliberate way: when no key is
/// configured, the middleware lets the request through (it guards a host that is entirely
/// internal), whereas this filter lets it through **only outside production**. An endpoint that
/// provisions a super-user must fail closed — a deployment that forgot to set the key should
/// refuse the call, not publish it.
/// </summary>
public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var config = services.GetRequiredService<IConfiguration>();
        var env = services.GetRequiredService<IHostEnvironment>();
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<ApiKeyEndpointFilter>();

        var expected = config[ApiKeyValidator.ConfigKey];
        var path = context.HttpContext.Request.Path;

        if (string.IsNullOrWhiteSpace(expected))
        {
            if (env.IsDevelopment())
                return await next(context);

            logger.LogError(
                "{ConfigKey} is not configured, so {Path} is refused. This endpoint provisions a "
                + "privileged account and will not run unprotected outside Development. Set "
                + "{ConfigKey} (ApiKey__ in the environment) and retry.",
                ApiKeyValidator.ConfigKey, path, ApiKeyValidator.ConfigKey);

            return Results.Problem(
                detail: "This endpoint requires an API key, and none is configured on the server.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var provided = context.HttpContext.Request.Headers[ApiKeyValidator.HeaderName].FirstOrDefault();

        if (!ApiKeyValidator.Matches(expected, provided))
        {
            logger.LogWarning(
                "API key {Outcome} on {Method} {Path} from {RemoteIp}",
                provided is null ? "missing" : "invalid",
                context.HttpContext.Request.Method,
                path,
                context.HttpContext.Connection.RemoteIpAddress);

            return Results.Json(
                new { error = "A valid X-Api-Key header is required." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}

public static class ApiKeyEndpointFilterExtensions
{
    /// <summary>
    /// Requires <c>X-Api-Key</c> to match the <c>ApiKey</c> configuration value. Outside
    /// Development the endpoint is refused when no key is configured.
    /// </summary>
    public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter<TBuilder, ApiKeyEndpointFilter>();
}
