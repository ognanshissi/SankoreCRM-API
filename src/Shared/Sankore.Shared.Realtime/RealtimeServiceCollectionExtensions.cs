namespace Sankore.Shared.Realtime;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel.Realtime;

/// <summary>
/// Everything a host must do to have a working push channel — and the four things it is easy to
/// do in a way that half works.
///
/// <para>
/// <b>1. The backplane.</b> Registered when a Redis connection string is present, and skipped
/// otherwise — with a warning at start-up, from <see cref="LogRealtimeBackplane"/>. A hub keeps its connections in the memory of the process that
/// accepted them, so without a backplane an "incoming call" pushed from the instance that
/// received the webhook reaches only the agents connected to THAT instance. The symptom is a
/// screen pop that works for some agents and not others, which reads as a flaky browser — the
/// one failure mode nobody diagnoses from the outside. Single-instance development is the one
/// case where its absence is correct, which is exactly where the connection string is also
/// absent, so the two line up on their own.
/// </para>
///
/// <para>
/// <b>2. The token does not travel in a header on a websocket.</b> The browser's WebSocket API
/// cannot set one, so SignalR puts the token in the query string as <c>access_token</c>. The JWT
/// handler has to be told to look there — see <see cref="UseRealtimeAccessToken"/> — and it must
/// be told to look there <b>only on the hub path</b>: a query-string token on an ordinary API
/// route would be a credential written into every access log and proxy trace on the way.
/// </para>
///
/// <para>
/// <b>3. CORS already works and must not be "fixed".</b> The default policy carries
/// <c>AllowCredentials</c> and refuses <c>*</c> at boot, which is precisely what a credentialed
/// websocket handshake needs. Adding an any-origin policy for the hub would quietly make the
/// cookie-and-credential story wrong for the whole API.
/// </para>
///
/// <para>
/// <b>4. The hub must be mapped BEFORE the OpenAPI document is emitted</b>, like every other
/// route: the document is built from the endpoint data sources at that line, and anything mapped
/// after it is missing from the generated client without a single failure. A hub contributes no
/// OpenAPI path of its own, so the only visible symptom of getting this wrong would be none at
/// all — which is why it is written down here rather than discovered.
/// </para>
/// </summary>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// The Aspire / docker-compose connection-string name. The same one
    /// <c>AddRedisDistributedCache("redis")</c> uses, on purpose: the backplane opens a SECOND
    /// multiplexer against the same server, which is how SignalR is meant to be deployed, and a
    /// separate setting would be a second thing to get wrong in every environment.
    /// </summary>
    private const string RedisConnectionName = "redis";

    public static IServiceCollection AddSankoreRealtime(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var signalR = services.AddSignalR(options =>
        {
            // The browser is told what went wrong in Development only. A hub exception's message
            // is server-side detail — a connection string, a table name — and SignalR's default
            // is already to withhold it; this says so rather than relying on the default.
            options.EnableDetailedErrors = false;
        });

        var redis = config.GetConnectionString(RedisConnectionName);

        if (!string.IsNullOrWhiteSpace(redis))
        {
            signalR.AddStackExchangeRedis(redis, options =>
            {
                // Every instance of this deployment shares one channel prefix, and only this
                // deployment: two environments pointed at one Redis (a staging API and a
                // production one, which happens) would otherwise deliver each other's pushes.
                options.Configuration.ChannelPrefix =
                    StackExchange.Redis.RedisChannel.Literal("sankore-realtime");
            });
        }

        // What was decided, for LogRealtimeBackplane to report after the host is built. The same
        // arrangement CorsSetup uses, and for the same reason: there is no logger yet at this
        // point, and a channel that silently half works is the failure mode worth a line.
        services.AddSingleton(new RealtimeBackplaneReport(
            Registered: !string.IsNullOrWhiteSpace(redis), ConnectionName: RedisConnectionName));

        // TryAdd, so a host that registered NullRealtimeNotifier first keeps it — the way a host
        // deliberately switches the channel off without removing this call.
        services.TryAddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();

        return services;
    }

    /// <summary>
    /// Logged once at start-up.
    ///
    /// <para>
    /// A backplane that is simply absent is the failure mode that costs a day: every agent's
    /// screen pop works in development and in a single-instance staging, and in production half
    /// the agency stops getting them — which is reported as a flaky browser, because that is
    /// exactly what it looks like from a desk.
    /// </para>
    /// </summary>
    public static void LogRealtimeBackplane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var report = app.Services.GetService<RealtimeBackplaneReport>();

        if (report is null)
        {
            // AddSankoreRealtime was never called: the host mounts no hub. Nothing to say.
            return;
        }

        if (report.Registered)
        {
            app.Logger.LogInformation(
                "Realtime: the SignalR hub is mounted at {HubPath} with a Redis backplane.",
                SankoreRealtimeHub.Path);

            return;
        }

        app.Logger.LogWarning(
            "Realtime: the SignalR hub is mounted at {HubPath} with NO backplane — no "
            + "'{ConnectionName}' connection string. Correct for a SINGLE instance. On more than "
            + "one, a push reaches only the agents connected to the instance that sent it, and "
            + "the symptom is a screen pop that works for part of the agency.",
            SankoreRealtimeHub.Path, report.ConnectionName);
    }

    private sealed record RealtimeBackplaneReport(bool Registered, string ConnectionName);

    /// <summary>
    /// Registers the no-op notifier, for a host that mounts no hub (<c>Sankore.Admin</c>,
    /// <c>Sankore.Hangfire</c>) or has the channel switched off.
    ///
    /// <para>
    /// Explicit rather than implied by the absence of <see cref="AddSankoreRealtime"/>: a module
    /// takes <see cref="IRealtimeNotifier"/> as a required dependency, so a host that registers
    /// neither fails at the first resolution — which is a start-up failure naming a type nobody
    /// expected to be involved.
    /// </para>
    /// </summary>
    public static IServiceCollection AddNoRealtime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();

        return services;
    }

    /// <summary>
    /// Teaches the JWT handler to read <c>access_token</c> from the query string, <b>on the hub
    /// path only</b>.
    ///
    /// <para>
    /// Call it inside <c>AddJwtBearer</c>'s options, composing with whatever
    /// <c>OnMessageReceived</c> the host already has rather than replacing it — the platform
    /// scheme also carries an <c>OnTokenValidated</c> session check, and an events object assigned
    /// wholesale here would silently drop it.
    /// </para>
    /// </summary>
    public static JwtBearerOptions UseRealtimeAccessToken(this JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Events ??= new JwtBearerEvents();

        var existing = options.Events.OnMessageReceived;

        options.Events.OnMessageReceived = async ctx =>
        {
            if (existing is not null)
                await existing(ctx);

            // StartsWithSegments and not Contains: "/hubs/realtime" appearing anywhere in a path
            // would let a caller opt a query-string credential into any route by crafting one.
            if (!ctx.Request.Path.StartsWithSegments(SankoreRealtimeHub.Path))
                return;

            var token = ctx.Request.Query["access_token"].ToString();

            if (!string.IsNullOrWhiteSpace(token))
                ctx.Token = token;
        };

        return options;
    }

    /// <summary>
    /// Maps the hub. <b>Before the line that emits the OpenAPI document</b>, with every other
    /// route.
    ///
    /// <para>
    /// No <c>RequireRateLimiting</c>, and that is a decision rather than an omission: the
    /// platform's limiters are in-memory and per instance, so a policy here would bound
    /// handshakes per process and not per deployment — and a websocket is one handshake followed
    /// by a long-lived connection, which is not the shape a fixed window measures. What bounds
    /// this surface is <c>[Authorize]</c> on the hub and the fact that it exposes no method a
    /// client can call.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapSankoreRealtimeHub(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHub<SankoreRealtimeHub>(SankoreRealtimeHub.Path);

        return app;
    }
}
