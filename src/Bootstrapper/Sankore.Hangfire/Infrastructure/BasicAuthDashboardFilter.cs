namespace Sankore.Hangfire.Infrastructure;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using global::Hangfire.Dashboard;

/// <summary>
/// HTTP Basic authentication for the Hangfire dashboard.
///
/// Unlike <c>ApiKeyMiddleware</c>, which skips validation when no key is configured,
/// this filter <b>fails closed</b>: with no password configured it denies every
/// request. The dashboard exposes full job payloads and lets anyone delete, requeue
/// or trigger work, so an unconfigured deployment must not be an open one.
/// </summary>
public sealed class BasicAuthDashboardFilter(string? username, string? password, ILogger logger)
    : IDashboardAuthorizationFilter
{
    private const string Realm = "Sankore Jobs";

    private readonly byte[] _expectedUser = Encoding.UTF8.GetBytes(username ?? string.Empty);
    private readonly byte[] _expectedPassword = Encoding.UTF8.GetBytes(password ?? string.Empty);
    private readonly bool _configured = !string.IsNullOrWhiteSpace(password);

    public bool Authorize(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var httpContext = context.GetHttpContext();

        if (!_configured)
        {
            logger.LogError(
                "Hangfire dashboard credentials are not configured. Set Hangfire:Dashboard:Username "
                + "and Hangfire:Dashboard:Password (user-secrets or environment variables). "
                + "Denying access to {Path} until they are.",
                httpContext.Request.Path);

            return Challenge(httpContext);
        }

        if (!AuthenticationHeaderValue.TryParse(httpContext.Request.Headers.Authorization, out var header)
            || !"Basic".Equals(header.Scheme, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter))
        {
            return Challenge(httpContext);
        }

        string credentials;
        try
        {
            credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return Challenge(httpContext);
        }

        var separator = credentials.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return Challenge(httpContext);
        }

        // Compare both halves in fixed time, and always compare both rather than
        // short-circuiting, so response timing does not reveal which half was wrong.
        var userMatches = FixedTimeEquals(credentials[..separator], _expectedUser);
        var passwordMatches = FixedTimeEquals(credentials[(separator + 1)..], _expectedPassword);

        if (userMatches && passwordMatches)
        {
            return true;
        }

        logger.LogWarning(
            "Rejected Hangfire dashboard request to {Path} from {RemoteIp}: invalid credentials.",
            httpContext.Request.Path,
            httpContext.Connection.RemoteIpAddress);

        return Challenge(httpContext);
    }

    private static bool FixedTimeEquals(string provided, byte[] expected)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), expected);

    private static bool Challenge(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        httpContext.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
        return false;
    }
}
