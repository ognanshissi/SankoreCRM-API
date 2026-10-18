using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Auth;

public static class AdminAuthSetup
{
    public const string CorsPolicy = "admin-front";
    public const string LoginRateLimit = "admin-login";
    public const string RefreshRateLimit = "admin-refresh";
    public const string CorsSection = "Cors:AllowedOrigins";

    /// <summary>The admin front-end's dev origin (SankoreFront <c>npm run start:admin</c>).</summary>
    private const string DevOrigin = "http://localhost:4223";

    /// <param name="validateOnStart">
    /// False only for <c>--emit-openapi</c>: describing the routes must not require the signing
    /// key, any more than it requires the database.
    /// </param>
    public static IServiceCollection AddAdminAuthentication(
        this IServiceCollection services, IConfiguration config, bool validateOnStart)
    {
        var jwtOptions = services.AddOptions<AdminJwtOptions>()
            .Bind(config.GetSection(AdminJwtOptions.Section))
            .Validate(o => o.Validate(out _), $"{AdminJwtOptions.Section} is invalid: see AdminJwtOptions.Validate.");
        if (validateOnStart)
            jwtOptions.ValidateOnStart();

        services.AddScoped<AdminTokenService>();
        services.AddScoped<IPasswordHasher<PlatformAdmin>, PasswordHasher<PlatformAdmin>>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from the bound options rather than read from IConfiguration inline, so the
        // key is the validated one and a missing key fails the start-up instead of the first call.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AdminJwtOptions>>((o, jwt) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Value.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                    // Named after the scheme rather than the library default ("AuthenticationTypes.Federation"),
                    // so AdminClaims.IsPlatformAdmin can tell this identity from one a future scheme produces.
                    AuthenticationType = JwtBearerDefaults.AuthenticationScheme,
                };
            });

        services.AddAuthorization();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Per IP and in memory: it bounds a flood. The durable guessing bound is the account
            // lockout on PlatformAdmin, which survives a restart.
            options.AddPolicy(LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 5, QueueLimit = 0 }));

            // Looser than the login: several open tabs refresh on their own, and a refresh token is
            // 64 random bytes, so it is not something anybody guesses.
            options.AddPolicy(RefreshRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 30, QueueLimit = 0 }));
        });

        return services;
    }

    /// <summary>
    /// Same contract as the API's <c>CorsSetup</c>: origins from <c>Cors:AllowedOrigins</c>, the
    /// dev origin as the Development fallback, <c>*</c> refused. Before this host had no CORS at
    /// all, which was right while only Sankore.Api called it — a browser now does.
    /// </summary>
    public static IServiceCollection AddAdminCors(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env, ILogger logger)
    {
        var origins = config.GetSection(CorsSection).Get<string[]>()?
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .ToArray() ?? [];

        if (origins.Contains("*"))
            throw new InvalidOperationException(
                $"{CorsSection} contains '*'. List the admin front-end's origins explicitly.");

        if (origins.Length == 0)
        {
            if (env.IsDevelopment())
                origins = [DevOrigin];
            else
                logger.LogWarning(
                    "{Section} is empty: the admin front-end will be blocked by the browser. Set "
                    + "Cors__AllowedOrigins__0 to its origin.", CorsSection);
        }

        // Also the DEFAULT policy: the front-end now reads the registry routes too, and a policy
        // named on the auth group alone left the browser blocking every tenant call.
        services.AddCors(o =>
        {
            o.AddPolicy(CorsPolicy, Configure);
            o.AddDefaultPolicy(Configure);
        });

        void Configure(Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder p) => p
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod();

        return services;
    }
}
