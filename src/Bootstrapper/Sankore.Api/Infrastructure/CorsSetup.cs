namespace Sankore.Api.Infrastructure;

/// <summary>
/// CORS origins come from configuration, not from source.
///
/// They used to be a literal <c>http://localhost:4222</c> in Program.cs, which meant the browser
/// blocked every call from a deployed front-end and no environment variable could fix it — the
/// only remedy was a code change and a rebuild.
/// </summary>
internal static class CorsSetup
{
    /// <summary>Configuration key: a string array, e.g. <c>Cors:AllowedOrigins:0</c>.</summary>
    private const string OriginsKey = "Cors:AllowedOrigins";

    /// <summary>
    /// Configuration key: a string array of domain suffixes, e.g.
    /// <c>Cors__AllowedOriginSuffixes__0=.talisoftcrm.com</c>. Every institution of the free trial
    /// gets its own <c>https://&lt;name&gt;.talisoftcrm.com</c> the moment the registry creates it
    /// (<c>docs/essai-gratuit-socle.md</c>, lot 0): a literal list would have to be edited, and the
    /// API restarted, at every sign-up.
    /// </summary>
    private const string SuffixesKey = "Cors:AllowedOriginSuffixes";

    /// <summary>Kept as the Development fallback so a local front-end needs no configuration.</summary>
    private const string DevelopmentOrigin = "http://localhost:4222";

    public static IServiceCollection AddSankoreCors(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var origins = config.GetSection(OriginsKey).Get<string[]>() ?? [];

        origins = [..origins
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        // "*" cannot be combined with AllowCredentials, and the browser rejects the response at
        // runtime with a message that points at the browser rather than at this setting. Refusing
        // it at boot with the reason is cheaper than debugging that.
        if (origins.Contains("*"))
        {
            throw new InvalidOperationException(
                $"{OriginsKey} contains \"*\". A wildcard origin is incompatible with credentialed "
                + "requests, which this API uses. List the exact origins instead.");
        }

        var suffixes = ParseSuffixes(config.GetSection(SuffixesKey).Get<string[]>() ?? []);

        if (origins.Length == 0 && suffixes.Length == 0 && env.IsDevelopment())
            origins = [DevelopmentOrigin];

        // In Development, every institution is reached on its own local host (`<name>.localhost`
        // or `<name>.sankoreio.local`; the tenant is resolved from it), and the starting-data loads
        // create new ones at will: a literal list made each new demo institution's front-end fail
        // on CORS until somebody edited appsettings. Development only.
        var allowLocalSubdomains = env.IsDevelopment();
        var listed = new HashSet<string>(origins, StringComparer.OrdinalIgnoreCase);

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                // No configured origin means no cross-origin access — fail closed. Same-origin
                // callers and server-to-server clients are unaffected: CORS is a browser rule.
                if (origins.Length > 0 || suffixes.Length > 0 || allowLocalSubdomains)
                {
                    policy.SetIsOriginAllowed(origin =>
                              listed.Contains(origin.TrimEnd('/'))
                              || IsInstitutionSubdomain(origin, suffixes)
                              || (allowLocalSubdomains && IsLocalSubdomain(origin)))
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                }
            });
        });

        services.AddSingleton(new CorsOriginsReport(origins, suffixes));
        return services;
    }

    /// <summary>
    /// Logged once at start-up. A silent empty policy is the failure mode that costs an afternoon:
    /// the API answers every curl correctly while the browser blocks the front-end.
    /// </summary>
    public static void LogCorsOrigins(this WebApplication app)
    {
        var (origins, suffixes) = app.Services.GetRequiredService<CorsOriginsReport>();

        if (suffixes.Length > 0)
            app.Logger.LogInformation(
                "CORS: every https://<name>{Suffixes} origin is allowed (one label, no port).",
                string.Join(", https://<name>", suffixes));

        if (origins.Length > 0)
        {
            app.Logger.LogInformation(
                "CORS: {OriginCount} allowed origin(s): {Origins}",
                origins.Length, string.Join(", ", origins));
            if (app.Environment.IsDevelopment())
                app.Logger.LogInformation(
                    "CORS: Development also allows every http(s) origin on {Domains}.",
                    string.Join(", ", LocalDomainSuffixes));
            return;
        }

        if (suffixes.Length > 0)
            return;

        app.Logger.LogWarning(
            "CORS: no allowed origin is configured, so every cross-origin browser request will be "
            + "blocked. Set {OriginsKey} (Cors__AllowedOrigins__0=https://crm.example.com) if a "
            + "front-end is served from another origin.",
            OriginsKey);
    }

    /// <summary>
    /// The local domains an institution's front-end is served on in Development: <c>.localhost</c>
    /// (always the loopback) and <c>.sankoreio.local</c> (names the developers map to 127.0.0.1 in
    /// /etc/hosts, served over https by <c>npm start</c>).
    /// </summary>
    private static readonly string[] LocalDomainSuffixes = [".localhost", ".sankoreio.local"];

    /// <summary><c>http(s)://&lt;name&gt;.&lt;local domain&gt;[:port]</c> — a local institution's front-end.</summary>
    internal static bool IsLocalSubdomain(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && LocalDomainSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validates the configured suffixes at boot. A suffix is a leading dot and at least two labels
    /// (<c>.talisoftcrm.com</c>): <c>.com</c>, a bare <c>talisoftcrm.com</c> (which would also match
    /// <c>eviltalisoftcrm.com</c>) or a wildcard are refused with the reason rather than silently
    /// widening the policy.
    /// </summary>
    internal static string[] ParseSuffixes(IEnumerable<string> configured)
    {
        var suffixes = configured
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        foreach (var suffix in suffixes)
        {
            var labels = suffix.Length > 1 ? suffix[1..].Split('.') : [];
            if (!suffix.StartsWith('.')
                || labels.Length < 2
                || labels.Any(l => !IsDnsLabel(l)))
            {
                throw new InvalidOperationException(
                    $"{SuffixesKey} contains \"{suffix}\". A suffix is a leading dot followed by a "
                    + "registered domain, e.g. \".talisoftcrm.com\" — never \"*\", a bare domain or a "
                    + "top-level domain alone.");
            }
        }

        return suffixes;
    }

    /// <summary>
    /// <c>https://&lt;one label&gt;&lt;suffix&gt;</c>, default port, nothing else — the address the
    /// registry gives an institution of the free trial. https only: the address is served behind
    /// Cloudflare, and accepting http would let a network attacker on a plain-text page call the API
    /// with a victim's browser. One label only: <c>a.b.talisoftcrm.com</c> is never an institution.
    /// </summary>
    internal static bool IsInstitutionSubdomain(string origin, IReadOnlyCollection<string> suffixes)
    {
        if (suffixes.Count == 0
            || !Uri.TryCreate(origin.TrimEnd('/'), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var host = uri.Host.ToLowerInvariant();
        foreach (var suffix in suffixes)
        {
            if (host.Length <= suffix.Length || !host.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            if (IsDnsLabel(host[..^suffix.Length]))
                return true;
        }

        return false;
    }

    /// <summary>A–Z, 0–9 and inner hyphens, 1 to 63 characters, no dot.</summary>
    private static bool IsDnsLabel(string label) =>
        label.Length is > 0 and <= 63
        && label[0] != '-' && label[^1] != '-'
        && label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private sealed record CorsOriginsReport(string[] Origins, string[] Suffixes);
}
