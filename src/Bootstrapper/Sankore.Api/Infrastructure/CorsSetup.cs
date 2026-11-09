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

        if (origins.Length == 0 && env.IsDevelopment())
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
                if (origins.Length > 0 || allowLocalSubdomains)
                {
                    policy.SetIsOriginAllowed(origin =>
                              listed.Contains(origin.TrimEnd('/'))
                              || (allowLocalSubdomains && IsLocalSubdomain(origin)))
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                }
            });
        });

        services.AddSingleton(new CorsOriginsReport(origins));
        return services;
    }

    /// <summary>
    /// Logged once at start-up. A silent empty policy is the failure mode that costs an afternoon:
    /// the API answers every curl correctly while the browser blocks the front-end.
    /// </summary>
    public static void LogCorsOrigins(this WebApplication app)
    {
        var origins = app.Services.GetRequiredService<CorsOriginsReport>().Origins;

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

    private sealed record CorsOriginsReport(string[] Origins);
}
