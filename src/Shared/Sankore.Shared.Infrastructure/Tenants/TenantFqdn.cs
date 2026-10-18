namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// The one place that says what a tenant's FQDN looks like, shared by the two hosts that write
/// one: Sankore.Admin seeds <c>tenants.fqdn</c>, Sankore.Api looks a tenant up by it to provision
/// the system user. Two copies of these three lines would be two chances to disagree on a
/// trailing slash, and the symptom of a disagreement is every request answering
/// 400 "Cannot determine tenant" with nothing wrong in either log.
/// </summary>
public static class TenantFqdn
{
    /// <summary>
    /// Trims, drops a trailing slash, lower-cases.
    ///
    /// <para>
    /// The slash is the one that bites: clients stamp calls with <c>location.origin</c>, which
    /// never carries one, while an operator copying a URL from a browser bar usually does — and
    /// <c>GetTenantByFqdnHandler</c> compares the whole string. The case is defensive: the column
    /// is <c>citext</c>, so the database would forgive it, but a predictable stored value is
    /// worth more than relying on that.
    /// </para>
    /// </summary>
    public static string Normalize(string? value) =>
        value?.Trim().TrimEnd('/').ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// True when the value is an absolute http(s) origin — scheme, host, optional port, nothing
    /// else. A bare host (<c>crm.example.com</c>) is NOT one, and that is the point: stored as a
    /// tenant's FQDN it can never match what a browser sends.
    /// </summary>
    public static bool IsOrigin(string? value, out string? problem)
    {
        problem = null;
        var normalized = Normalize(value);

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            problem =
                $"\"{value}\" is not an absolute http(s) origin. Give the front-end's own origin, "
                + "scheme included, e.g. https://crm.example.com — clients send location.origin, "
                + "so a bare host is never matched.";
            return false;
        }

        if (uri.AbsolutePath != "/")
        {
            problem = $"\"{value}\" carries a path. An origin is scheme, host and port only.";
            return false;
        }

        return true;
    }
}
