namespace Sankore.Shared.Infrastructure.Auth;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// One place that decides whether a presented API key matches the configured one.
///
/// Shared by <see cref="ApiKeyMiddleware"/> (whole-host protection, used by Sankore.Admin) and
/// <see cref="ApiKeyEndpointFilter"/> (a single endpoint on a host that is otherwise JWT-based),
/// so the two cannot drift on what "matches" means.
/// </summary>
public static class ApiKeyValidator
{
    public const string HeaderName = "X-Api-Key";

    /// <summary>Configuration key holding the expected value, at the configuration root.</summary>
    public const string ConfigKey = "ApiKey";

    /// <summary>
    /// Compares in constant time. An ordinary string comparison returns as soon as two bytes
    /// differ, which leaks the length of the shared prefix to anyone able to time the responses —
    /// enough, over many requests, to recover the key byte by byte.
    /// </summary>
    public static bool Matches(string? expected, string? provided)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }
}
