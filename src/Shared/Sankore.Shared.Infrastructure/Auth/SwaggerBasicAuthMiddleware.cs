namespace Sankore.Shared.Infrastructure.Auth;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// HTTP Basic authentication in front of the Swagger UI and the OpenAPI document.
///
/// <para>
/// The document is a complete map of the API — every route, every payload shape, every enum
/// member — and the UI is a ready-made client for it. Neither reads a row, so serving them is
/// harmless only while nobody but the team can reach them; the moment a non-Development host
/// exposes them that stops being true, which is why <see cref="SwaggerExposure"/> keeps them off
/// by default there and this middleware guards them when they are switched on.
/// </para>
///
/// <para>
/// It follows <see cref="ApiKeyEndpointFilter"/>'s rule rather than <see cref="ApiKeyMiddleware"/>'s:
/// with no password configured it lets the request through <b>in Development only</b> (a local
/// <c>dotnet run</c> must not start asking for credentials) and answers 503 everywhere else. An
/// operator who turned Swagger on and forgot the password gets no documentation rather than
/// unprotected documentation.
/// </para>
/// </summary>
public sealed class SwaggerBasicAuthMiddleware(
    RequestDelegate next,
    IConfiguration config,
    IHostEnvironment env,
    ILogger<SwaggerBasicAuthMiddleware> logger)
{
    private const string Realm = "Sankore API documentation";

    /// <summary>Configuration keys holding the expected credentials.</summary>
    public const string UsernameConfigKey = "Swagger:Auth:Username";
    public const string PasswordConfigKey = "Swagger:Auth:Password";

    /// <summary>
    /// Guarded prefixes. <c>/swagger</c> covers both the UI and <c>/swagger/v1/swagger.json</c>;
    /// <c>/openapi</c> covers the document a future <c>MapOpenApi()</c> would serve, so turning
    /// that on cannot quietly publish the same map through another path.
    /// </summary>
    private static readonly string[] GuardedPrefixes = ["/swagger", "/openapi"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGuarded(context.Request.Path))
        {
            await next(context);
            return;
        }

        var expectedUser = config[UsernameConfigKey];
        var expectedPassword = config[PasswordConfigKey];

        if (string.IsNullOrWhiteSpace(expectedPassword))
        {
            if (env.IsDevelopment())
            {
                await next(context);
                return;
            }

            logger.LogError(
                "{PasswordConfigKey} is not configured, so {Path} is refused. Swagger is exposed "
                + "on this host but will not be served unprotected outside Development. Set "
                + "{UsernameConfigKey} and {PasswordConfigKey} (Swagger__Auth__Password in the "
                + "environment), or turn Swagger off with Swagger:Enabled=false.",
                PasswordConfigKey, context.Request.Path, UsernameConfigKey, PasswordConfigKey);

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "API documentation requires credentials, and none are configured on the server.",
            });
            return;
        }

        if (!TryReadCredentials(context.Request, out var providedUser, out var providedPassword))
        {
            Challenge(context);
            return;
        }

        // Both halves are always compared, and each in fixed time, so neither the response timing
        // nor the work done reveals which half was wrong.
        var userMatches = FixedTimeEquals(providedUser, expectedUser ?? string.Empty);
        var passwordMatches = FixedTimeEquals(providedPassword, expectedPassword);

        if (userMatches && passwordMatches)
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "Rejected API documentation request to {Path} from {RemoteIp}: invalid credentials.",
            context.Request.Path,
            context.Connection.RemoteIpAddress);

        Challenge(context);
    }

    private static bool IsGuarded(PathString path) =>
        GuardedPrefixes.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadCredentials(HttpRequest request, out string user, out string password)
    {
        user = string.Empty;
        password = string.Empty;

        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !"Basic".Equals(header.Scheme, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0) return false;

        user = decoded[..separator];
        password = decoded[(separator + 1)..];
        return true;
    }

    private static bool FixedTimeEquals(string provided, string expected)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    private static void Challenge(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
    }
}

public static class SwaggerBasicAuthExtensions
{
    /// <summary>
    /// Puts HTTP Basic authentication in front of <c>/swagger</c> and <c>/openapi</c>. Must be
    /// registered <b>before</b> <c>UseSwagger()</c> / <c>UseSwaggerUI()</c>, or those serve the
    /// request before the gate runs.
    /// </summary>
    public static IApplicationBuilder UseSwaggerBasicAuth(this IApplicationBuilder app)
        => app.UseMiddleware<SwaggerBasicAuthMiddleware>();
}
