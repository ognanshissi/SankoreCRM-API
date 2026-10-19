using System.Linq.Expressions;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Sankore.Api.Features.Audit.GetAuditEntries;
using Sankore.Api.Features.Bootstrap;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Api.Infrastructure;
using Sankore.Api.Infrastructure.Audit;
using Sankore.Modules.Leads;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Administration;
using Sankore.Modules.Workflow;
using Sankore.Modules.Notifications;
using Sankore.Modules.Customers;
using Sankore.Modules.Kyc;
using Sankore.Modules.Integration;
using Sankore.Shared.ObjectStorage;
using Sankore.Api.Features.ObjectStorage;
using Sankore.Api.Features.ObjectStorage.MigrateObjects;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Notifications.Infrastructure.Consumers;
using Sankore.Modules.Workflow.Infrastructure.Consumers;
using Sankore.Modules.Workflow.Infrastructure.Triggers;
using Hangfire;
using Hangfire.PostgreSql;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.DataProtection;
using Sankore.Shared.Infrastructure.Logging;
using Sankore.Shared.Infrastructure.Localization;
using Sankore.Shared.Infrastructure.Secrets;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Sankore.Shared.Realtime;

// Emitting the OpenAPI document is a build-time concern, not a run. The front-end client in
// SankoreFront is generated from swaggers/sankore-crm-api-swagger.json, and producing that file
// used to mean booting the whole API — which means a reachable, already-migrated PostgreSQL, for
// a document that describes routes and schemas and depends on no row anywhere. With this flag the
// host builds, maps its endpoints, writes the document and exits:
//
//   dotnet run --project src/Bootstrapper/Sankore.Api -- --emit-openapi <path>
//
// Nothing is migrated and nothing is seeded in that mode, deliberately: a developer regenerating
// a client must not be able to alter a database by doing so.
var emitOpenApiTo = OpenApiEmitter.OutputPathFrom(args);

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Serialize enums as their string names in all HTTP JSON responses and requests.
// This keeps the wire format human-readable and consistent with the Swagger schema.
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.GuidConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.NullableGuidConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.TimeSpanConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.DateOnlyConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.NullableDateOnlyConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.SourceSettingsJsonConverter());
    opts.SerializerOptions.Converters.Add(
        new Sankore.Api.Infrastructure.ConnectionSettingsJsonConverter());
});
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();

// Origins from Cors:AllowedOrigins — see CorsSetup for why they are not literals here.
builder.Services.AddSankoreCors(builder.Configuration, builder.Environment);

builder.AddSeqEndpoint("seq");
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration;
    o.CombineLogs = true;
});

// ---------------------------------------------------------------------
// 1. Cross-cutting infrastructure (auth, MediatR pipeline, messaging bus)
// ---------------------------------------------------------------------

builder.Services.AddHttpContextAccessor();

// Context-aware registrations: background jobs (Hangfire) set BackgroundJobContext
// via AsyncLocal; HTTP requests fall back to the standard JWT-based implementations.
builder.Services.AddScoped<ICurrentUser>(sp =>
{
    if (BackgroundJobContext.CurrentUser is { } bgUser)
        return bgUser;
    return new HttpContextCurrentUser(sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>());
});
builder.Services.AddScoped<ITenantContext>(sp =>
{
    if (BackgroundJobContext.CurrentTenant is { } bgTenant)
        return bgTenant;
    return new HttpTenantContext(sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>());
});
builder.Services.AddTenantStore(builder.Configuration);

// The system user SystemUserSeeder provisions at start-up. No ValidateOnStart: a bad value is
// reported by the seeder and the API still comes up, so /health answers and the manual
// POST /api/v1/users/create-root stays reachable.
builder.Services.Configure<SystemUserOptions>(
    builder.Configuration.GetSection(SystemUserOptions.Section));
builder.Services.AddLanguageResolution();
builder.Services.AddLocalization(opts => opts.ResourcesPath = "Resources");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtConfig = builder.Configuration.GetSection("Jwt");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtConfig["Issuer"],
            ValidAudience = jwtConfig["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtConfig["SigningKey"]!))
        };
        // Le jeton ne voyage PAS en en-tête sur un websocket : l'API WebSocket du navigateur ne
        // permet pas d'en poser un, donc SignalR le met en query sous `access_token`. Appelé
        // AVANT l'affectation ci-dessous ? Non : UseRealtimeAccessToken compose avec
        // OnMessageReceived existant, et comme `options.Events` est RÉASSIGNÉ juste après, l'appel
        // doit venir APRÈS — sinon le OnMessageReceived qu'il installe est jeté en silence et
        // aucune connexion au hub ne s'authentifie. La règle ne s'applique qu'au chemin du hub :
        // un jeton en query sur une route ordinaire serait un identifiant écrit dans chaque log
        // d'accès et chaque trace de proxy du parcours.
        //
        // Une session coupée depuis « Sessions actives » refuse aussi les jetons d'accès qu'elle
        // avait déjà reçus. Sans claim sid (jeton antérieur), rien ne change.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                if (SessionClaim.Read(ctx.Principal) is not { } sessionId) return;
                // Lu sur le jeton : HttpContext.User n'est pas encore renseigné à ce stade, donc
                // ITenantContext non plus.
                if (!Guid.TryParse(ctx.Principal!.FindFirst("tenant_id")?.Value, out var tenantId))
                {
                    ctx.Fail("Missing tenant.");
                    return;
                }
                var validator = ctx.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
                if (!await validator.IsActiveAsync(tenantId, sessionId, ctx.HttpContext.RequestAborted))
                    ctx.Fail("Session ended.");
            },
        };

        // Après l'affectation d'options.Events, délibérément : cette extension compose avec le
        // OnMessageReceived déjà présent au lieu de le remplacer, et l'objet Events vient d'être
        // recréé au-dessus.
        options.UseRealtimeAccessToken();
    });

// ── The remote-KYC session scheme (KRM-B-05, §4.3) ─────────────────────────
//
// A SECOND bearer scheme, with its own signing key and its own audience, for the token an
// anonymous customer receives after identifying themselves on the public page. The separation is
// the security property: a session token's signature does not validate under the platform scheme,
// so it is refused by every back-office endpoint, and a platform token does not validate here, so
// it cannot drive a customer's upload journey.
//
// The key is derived from Jwt:SigningKey by default — see KycRemoteSessionOptions for why that is
// both safe and the only option that does not break an existing deployment on upgrade.
var kycRemoteSessionOptions =
    Sankore.Modules.Kyc.KycModule.ReadRemoteSessionOptions(builder.Configuration);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(Sankore.Modules.Kyc.KycModule.RemoteSessionScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = kycRemoteSessionOptions.Issuer,
            ValidAudience = Sankore.Modules.Kyc.KycModule.RemoteSessionAudience,
            IssuerSigningKey = new SymmetricSecurityKey(kycRemoteSessionOptions.SigningKey),
            // No clock skew. A 20-minute session with the default five minutes of tolerance would
            // be a 25-minute one, and the whole point of the window is that it is short.
            ClockSkew = TimeSpan.Zero,
        };

        // No OnTokenValidated session check: there is no ISessionValidator for a customer, and the
        // revocation that matters is done per request by KycRemoteGate, which re-reads the
        // invitation. A signed session whose link has been spent or withdrawn stops working on its
        // very next call.
    });

builder.Services.AddAuthorization(options =>
{
    // Pins the SCHEME as well as requiring authentication: without AddAuthenticationSchemes the
    // policy would fall back to the default scheme and a platform token would satisfy it.
    options.AddPolicy(Sankore.Modules.Kyc.KycModule.RemoteSessionScheme, policy => policy
        .AddAuthenticationSchemes(Sankore.Modules.Kyc.KycModule.RemoteSessionScheme)
        .RequireAuthenticatedUser()
        // Third independent reason, after the key and the audience: a token issued for anything
        // else is refused even if somebody reuses the key by mistake.
        .RequireClaim("scope", "kyc:remote"));
});

builder.Services.AddSankoreAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict limit for unauthenticated auth endpoints (login, forgot-password, etc.)
    // 5 requests per minute per IP address — mitigates brute-force and enumeration attacks.
    options.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 5,
                QueueLimit = 0,
            }));

    // Standard limit for all authenticated API endpoints.
    // 200 requests per minute keyed by user ID (JWT sub); falls back to IP for anonymous calls.
    options.AddPolicy("api", ctx =>
    {
        var userId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var key = userId ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            PermitLimit = 200,
            QueueLimit = 0,
        });
    });

    // Integration webhooks: per IP AND per connection (INT-20).
    //
    // It cannot reuse "ingest-key": that policy partitions on the route value `publicKey`, which
    // this route does not have, so every integration webhook reaching one instance would share a
    // single 10/min window — a back office emitting one notification per customer event would be
    // throttled almost immediately, and the throttling would look like the CBS being quiet.
    //
    // 60/min because the work a verified webhook triggers is one idempotent re-projection of a
    // customer we already know; the signature check is what keeps an unauthenticated caller from
    // spending it, and the limit is there to bound a misbehaving partner rather than to authorise.
    options.AddPolicy("integration-webhook", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var connection = ctx.Request.RouteValues["connectionId"]?.ToString() ?? "none";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"integration-webhook:{ip}:{connection}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 60,
                QueueLimit = 0,
            });
    });

    // Téléphonie (TEL-B-07). Sa propre politique, et pas une branche de celle ci-dessus : un
    // fournisseur de téléphonie émet plusieurs événements PAR APPEL et par agent — sonnerie,
    // décroché, raccroché — donc une centaine d'agents en journée dépasse 60/min sans que rien
    // n'aille mal. Partager la fenêtre de la banque centrale étranglerait la famille la plus
    // active des deux, et un webhook étranglé ressemble à une ligne silencieuse.
    //
    // 600/min par (IP, connexion). C'est une borne contre un partenaire qui déraille, pas une
    // autorisation : ce qui autorise, c'est la signature, vérifiée avant que le corps soit lu, et
    // ce qui empêche un rejeu de travailler deux fois, c'est l'index unique de tel_webhook_event.
    options.AddPolicy(
        Sankore.Modules.Integration.IntegrationModule.TelephonyWebhookRateLimitPolicy,
        ctx =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var connection = ctx.Request.RouteValues["connectionId"]?.ToString() ?? "none";

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"telephony-webhook:{ip}:{connection}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    PermitLimit = 600,
                    QueueLimit = 0,
                });
        });

    // Remote KYC public surface (KRM-B-05, §8). Per IP.
    //
    // 30/min: a customer walking the journey makes a handful of calls, and each photograph is one
    // upload — a burst of four or five is normal, a hundred is not. It bounds the FLOOD only; the
    // attack that matters against this surface is guessing a date of birth, and that is bounded by
    // the per-link failed-attempt counter on the invitation, which is durable and per-token rather
    // than per-instance and resettable by a deployment.
    options.AddPolicy("kyc-public", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"kyc-public:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 30,
                QueueLimit = 0,
            }));

    // SMS delivery callbacks. Per IP, and generous: a provider sends one callback per message
    // and can batch a backlog after an outage of its own, so a tight window would drop status
    // updates the platform then never hears about again — these are not retried indefinitely.
    // The gate that matters here is not the rate but the signature: Twilio's HMAC and Brevo's
    // derived URL token, both checked before anything is written.
    options.AddPolicy("sms-webhooks", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"sms-webhooks:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 300,
                QueueLimit = 0,
            }));

    // Web ingest: 10 req/min per IP+key (US-F13.37-BE-12)
    options.AddPolicy("ingest-key", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = ctx.Request.RouteValues["publicKey"]?.ToString() ?? "none";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"ingest:{ip}:{key}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 10,
                QueueLimit = 0,
            });
    });
});

// MediatR — register Bootstrapper's own handlers (audit query, etc.)
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// MediatR pipeline behaviors: order matters (outermost first).
// Logging -> Validation -> Transaction -> Audit -> [Handler]
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
// Agency perimeter: rejects any IAgencyScopedRequest whose TargetAgencyId falls outside the
// caller's perimeter BEFORE a transaction is opened. Inert for every other request.
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AgencyAuthorizationBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));

// Audit — dedicated DbContext + real writer.
// AddDbContextFactory registers both the factory (singleton) and a scoped
// DbContext so that EF tooling and the migration startup code can resolve it.
var connectionString = builder.Configuration.GetConnectionString("Database")!;
builder.Services.AddDbContextFactory<AuditDbContext>(opts =>
    opts.UseNpgsql(connectionString,
        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "audit"))
        .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IAuditWriter, SqlAuditWriter>();

// Data Protection key ring — PostgreSQL-backed, fixed application name.
// Identity signs every account-activation and password-reset token with this ring. The framework
// default keeps it in the container filesystem, so a redeploy silently invalidated every
// activation link already in a user's inbox. See SankoreDataProtection.
builder.Services.AddSankoreDataProtection(
    builder.Configuration,
    opts => opts.UseNpgsql(connectionString,
            b => b.MigrationsHistoryTable("__EFMigrationsHistory", "dataprotection"))
        .UseSnakeCaseNamingConvention());

// Secrets vault — AES-256-GCM encrypted, PostgreSQL-backed.
// AddSecretsVault validates Secrets:EncryptionKey at start-up: the app refuses to boot without
// a usable key rather than throwing on the first write.
builder.Services.AddSecretsVault(
    builder.Configuration,
    opts => opts.UseNpgsql(connectionString,
            b => b.MigrationsHistoryTable("__EFMigrationsHistory", "secrets"))
        .UseSnakeCaseNamingConvention());

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<Sankore.Modules.Kyc.Features.Limits.Consumers.KycTierChangedCacheConsumer>();

    x.AddConsumer<Sankore.Modules.Kyc.Features.Files.Consumers.ClientCreatedKycConsumer>();
    x.AddConsumer<Sankore.Modules.Kyc.Features.Files.Consumers.KycRequestedConsumer>();

    // A rise in a client's PEP exposure owes a KYC review (BNF-B-13). Lives in M02 because
    // RaiseKycReviewCommand is internal to it, and its handler already announces the review
    // through the same sweep a periodic one goes through.
    x.AddConsumer<Sankore.Modules.Kyc.Features.Reviews.Consumers.ClientPepExposureRaisedConsumer>();

    // KRM-B-04: withdraws a customer's remote-collection link when the email it was posted to, or
    // the date of birth it is checked against, changes in M01.
    x.AddConsumer<Sankore.Modules.Kyc.Features.RemoteInvitations.Consumers.ClientContactChangedInvitationConsumer>();

    // KRM-B-08: applies the product's policy once a verification comes back — approving a clean
    // remote file with no human, or handing it to one with a coded reason. Registered in M02 and
    // consuming M02's own event, which is what makes the decision durable across a replay.
    x.AddConsumer<Sankore.Modules.Kyc.Features.Routing.Consumers.KycVerificationCompletedRoutingConsumer>();

    // KRM-B-09: tells the responsible agent (or their branch manager) that a remote file is
    // waiting for them. A consumer and not a line in the handler, because QueueEmailAsync is not
    // part of M02's transaction — §8 of the specification asks for exactly this shape.
    x.AddConsumer<Sankore.Modules.Kyc.Features.Routing.Consumers.KycRemoteReviewRequiredConsumer>();

    // KRM-B-10: tells the CUSTOMER how their file ended up. On the validation and rejection events
    // rather than on the routing, so an automatic and a manual approval produce the same message.
    x.AddConsumer<Sankore.Modules.Kyc.Features.Routing.Consumers.KycRemoteOutcomeConsumer>();

    // Drops M08's SMS provider cache when a tenant reconfigures its channel. Its own consumer
    // beside the email one so an email change does not invalidate the SMS entry and the reverse.
    x.AddConsumer<Sankore.Modules.Notifications.Infrastructure.Consumers.TenantSmsSettingsChangedConsumer>();

    x.AddConsumer<Sankore.Modules.Leads.Features.Consumers.LeadAutoDispatchConsumer,
        Sankore.Modules.Leads.Features.Consumers.LeadAutoDispatchConsumerDefinition>();
    x.AddConsumer<LeadDispatchedTaskConsumer>();
    x.AddConsumer<LeadDispatchingFailedTaskConsumer>();
    // Absence déclarée par un agent (M12) : ses tâches ouvertes passent au remplaçant (M13).
    x.AddConsumer<Sankore.Modules.Leads.Features.Consumers.AgentAbsenceTaskTransferConsumer>();

    // TEL-B-11/B-12 : la qualification d'un appel devient l'activité « Appel » du lead et arrête
    // son horloge de premier contact. Sans ce consommateur, un agent qualifie ses appels et le
    // lead n'en garde aucune trace — et son SLA reste en retard pour une conversation qui a eu
    // lieu.
    x.AddConsumer<Sankore.Modules.Leads.Features.Consumers.TelephonyCallWrappedUpConsumer>();

    // TEL-B-17 : le PREMIER consommateur de ClientAnonymizedEvent. M01 le publie depuis ses
    // travaux de rétention et personne n'écoutait — ce qui était juste tant que rien d'autre ne
    // détenait de données personnelles d'un client hors du schéma customers. Un enregistrement de
    // la voix de quelqu'un est le contre-exemple le plus net.
    x.AddConsumer<Sankore.Modules.Integration.Features.Telephony.Recordings
        .ClientAnonymizedRecordingConsumer>();

    // TEL-B-19 : la qualification d'un appel rafraîchit le jour de cet appel dans le modèle de
    // lecture des indicateurs. Ce n'est PAS un incrément — l'événement dit quel jour a changé, et
    // le recalcul recompte ce jour depuis le journal — donc un message redélivré, deux agents qui
    // qualifient en même temps et un appel qualifié deux fois donnent les mêmes chiffres.
    x.AddConsumer<Sankore.Modules.Integration.Features.Telephony.Stats
        .TelephonyStatsConsumer>();

    // Notification module consumers
    x.AddConsumer<TenantNotificationSettingsChangedConsumer>();

    // Workflow module consumers
    x.AddConsumer<WorkflowTriggerConsumer>();
    x.AddConsumer<ChildWorkflowCompletedConsumer>();

    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.KycValidatedConsumer>();

    // Integration module — the onboarding chain (INT-14). KycValidatedEvent is consumed here AND
    // by M01 above; each module has its own inbox table, and this module's guard additionally
    // derives a per-consumer id so its two consumers can share one event.
    x.AddConsumer<Sankore.Modules.Integration.Features.Onboarding.KycValidatedOnboardingConsumer>();
    x.AddConsumer<Sankore.Modules.Integration.Features.Onboarding.CbsCustomerCreatedOnboardingConsumer>();

    // BNF-B-16 — the subscription chain, both directions. Integration turns a signed holding into
    // a queued command; M01 turns the far end's answer back into Active or Rejected. Registering
    // one without the other leaves a holding stuck in PendingSync with nothing to move it.
    x.AddConsumer<Sankore.Modules.Integration.Features.Insurance.HoldingSubscription.HoldingSubscriptionConsumer>();

    // The ASS-05 chain, in both directions. Registered as a PAIR with the command that starts it:
    // a saga whose outcome consumers are missing sits in PendingDebit for ever with nothing to
    // move it — which is the defect lot 7 shipped for holdings and lot 8 had to close.
    x.AddConsumer<Sankore.Modules.Integration.Features.Insurance.SubscribePolicy.InsuranceSagaSucceededConsumer>();
    x.AddConsumer<Sankore.Modules.Integration.Features.Insurance.SubscribePolicy.InsuranceSagaRejectedConsumer>();

    // ASS-08's premium cycle, the same pair-or-nothing rule: an instalment left in Debiting is one
    // no job picks up again.
    x.AddConsumer<Sankore.Modules.Integration.Features.Insurance.Premiums.PremiumPaidConsumer>();
    x.AddConsumer<Sankore.Modules.Integration.Features.Insurance.Premiums.PremiumFailedConsumer>();

    // ASS-09: the insurer's acknowledgement of a declaration. Alone and NOT as a pair — there is
    // deliberately no "declaration rejected" consumer, because a rejected DeclareClaim command
    // means our transmission failed, not that the insurer refused the claim; INT-06's retry and
    // INT-07's rejection queue already own that. See the consumer's own remarks.
    x.AddConsumer<Sankore.Modules.Integration.Features.Insurance.Claims.ClaimDeclarationAcknowledgedConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Holdings.Consumers.HoldingSyncConfirmedConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Holdings.Consumers.HoldingSyncRejectedConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.KycRejectedConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.KycRiskLevelChangedConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.KycInitiatedConsumer>();
    // Development-only auto-validation stub; inert outside Development (US-M01-BE-13).
    x.AddConsumer<Sankore.Modules.Customers.Features.Lifecycle.Consumers.DevKycAutoValidateConsumer>();

    // 360° timeline projection (US-M01-BE-26). Adding a source module later means
    // adding a consumer here — the timeline model itself never changes.
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientCreatedTimelineConsumer>();
    // Welcomes a prospect who just became a client (lead conversions only).
    x.AddConsumer<Sankore.Modules.Customers.Features.LeadConversion.Consumers.ClientWelcomeEmailConsumer>();

    // A departing household member leaves designations in ToReview, so the rank no longer totals
    // 100% — the one state BNF-B-13 requires an agent be shown, and nothing else surfaces it.
    x.AddConsumer<Sankore.Modules.Customers.Features.Household.Consumers.HouseholdMemberRemovedAlertConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientActivatedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientSuspendedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientArchivedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientTransferredTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientsMergedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.ClientSegmentChangedTimelineConsumer>();
    x.AddConsumer<Sankore.Modules.Customers.Features.Timeline.Consumers.GroupMembershipChangedTimelineConsumer>();

    var useRabbitMq = builder.Configuration.GetValue<bool>("Messaging:UseRabbitMq");

    if (useRabbitMq)
    {
        // Aspire's connection string FIRST, the bare host name only as a fallback.
        //
        // The AppHost provisions the broker with `AddRabbitMQ("rabbitmq")` and hands it to this
        // project with `WithReference(rmq)`, which injects ConnectionStrings:rabbitmq —
        // amqp://guest:<generated password>@localhost:<random host port>. Neither half of that is
        // guessable: Aspire publishes 5672 on an ephemeral port and generates the password per run.
        // Reading only Messaging:RabbitMqHost therefore dialled amqp://guest:guest@localhost:5672,
        // where nothing listens, and the bus spent the whole session logging
        // "Connection Failed: rabbitmq://localhost/ ... Connection refused (127.0.0.1:5672)" while
        // every integration event stayed undelivered. The API answered HTTP throughout, which is
        // why it reads as noise rather than as an outage.
        //
        // The fallback is what docker-compose needs: that stack maps 5672 and keeps guest/guest, so
        // it has no connection string to inject.
        var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq");

        x.UsingRabbitMq((context, cfg) =>
        {
            if (!string.IsNullOrWhiteSpace(rabbitMqConnectionString))
                cfg.Host(new Uri(rabbitMqConnectionString));
            else
                cfg.Host(builder.Configuration["Messaging:RabbitMqHost"] ?? "localhost");

            // Not optional, and it was missing here while the in-memory branch had it: without
            // ConfigureEndpoints, MassTransit binds no receive endpoint for the consumers
            // registered above. A reachable broker would then have published every event into
            // exchanges nothing was reading — KYC files never opened on a client creation, leads
            // never auto-dispatched, timelines never projected — with no error anywhere, because
            // publishing to an exchange without a queue succeeds.
            cfg.ConfigureEndpoints(context);
        });
    }
    else
    {
        x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
    }
});

// Hangfire — background job processing (lead import, etc.)
builder.Services.AddHangfire(cfg =>
    cfg.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
       .UseSimpleAssemblyNameTypeSerializer()
       .UseRecommendedSerializerSettings()
       .UsePostgreSqlStorage(opts =>
           opts.UseNpgsqlConnection(connectionString)));
// Not in emit mode: the document describes routes, and the one thing that would still reach for
// PostgreSQL while writing it is this worker starting up.
if (emitOpenApiTo is null)
{
    // Named queues for the Integration module (INT-06, INT-09). Until this module existed every
    // job in the solution ran on "default" with the stock worker count, and that is exactly why
    // "default" has to stay first in this list: passing Queues REPLACES the default set, so
    // omitting it would silently stop every lead, KYC and customer job from being processed.
    //
    // The split matters because the three integration queues have different failure modes. A
    // batch job holds a whole file in memory and runs for minutes; a write job is a short
    // outbound call that must not queue behind it; a sync job is long but interruptible. One
    // shared pool lets a nightly batch starve the writes an agent is waiting on.
    builder.Services.AddHangfireServer(opts =>
    {
        opts.Queues =
        [
            "default",
            "integration-write",
            "integration-sync",
            "integration-batch",
        ];

        // Left at the framework default (ProcessorCount * 5) rather than pinned: the figure is
        // per host and this process also serves HTTP. Raise it deliberately, with a measurement.
    });
}

// Platform job: the one-shot copy of stored objects to the bucket. Transient like every module's
// job type — Hangfire activates it from the container.
builder.Services.AddTransient<MigrateObjectsJob>();

// Redis distributed cache — used by Notifications module for provider resolution
builder.AddRedisDistributedCache("redis");

// Le canal de push vers le navigateur (TEL-B-10) : le hub SignalR, plus son backplane Redis quand
// une chaîne de connexion existe. Sans backplane, une sonnerie poussée depuis l'instance qui a
// reçu le webhook n'atteint que les agents connectés à CELLE-LÀ — un screen pop qui marche pour la
// moitié de l'agence, et qui se lit comme un navigateur capricieux. AddSankoreRealtime prévient au
// démarrage quand il n'y en a pas.
builder.Services.AddSankoreRealtime(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();

#region Swagger Generation
builder.Services.AddSwaggerGen(options => 
{
    // Short names, except where two modules declare the same one — see SwaggerSchemaIds. Without
    // it the first such collision makes the WHOLE document ungenerable, which is a start-up
    // failure that only running the host reveals.
    options.CustomSchemaIds(Sankore.Api.Infrastructure.SwaggerSchemaIds.For);

    options.AddSecurityDefinition("BearerToken", new ()
    {
        Name = "Authorization",
        Description = "Token-based authentication using the Bearer scheme",
        Type = SecuritySchemeType.Http,
        In = ParameterLocation.Header,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new()
    {
        {
            new()
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "BearerToken" }
            },
            new List<string>()
        }
    });

    // Render every enum as a string schema with all valid member names listed.
    // Ensures Swagger UI shows "New | Open | Qualifying | ..." instead of "0 | 1 | 2 | ...".
    options.SchemaFilter<EnumSchemaFilter>();

    // Describe the two polymorphic settings hierarchies as oneOf + their discriminator.
    // Reflection alone documents the abstract base, which omits the discriminator the HTTP
    // converters resolve through — see PolymorphicSettingsSchemaFilter.
    options.SchemaFilter<ConnectionSettingsSchemaFilter>();
    options.SchemaFilter<SourceSettingsSchemaFilter>();

    options.UseInlineDefinitionsForEnums();
});
#endregion

foreach (var concern in ObjectStorageConcerns.All)
{
    builder.Services.AddObjectBackend(
        builder.Configuration,
        concern.Name,
        concern.ResolveLocalRoot(builder.Configuration, builder.Environment));
}

builder.Services.AddAdministrationModule(builder.Configuration);
builder.Services.AddLeadsModule(builder.Configuration);
builder.Services.AddWorkflowModule(builder.Configuration);
builder.Services.AddNotificationsModule(builder.Configuration);
builder.Services.AddCustomersModule(builder.Configuration);      // M01

builder.Services.AddKycModule(builder.Configuration);            // M02

builder.Services.AddIntegrationModule(builder.Configuration);    // Integration (CBS + assurance)

// Every core-banking adapter, in one method so that a shipped-but-unregistered adapter is a
// failing test rather than a support ticket — see IntegrationAdapterComposition.
builder.Services.AddIntegrationAdapters(builder.Configuration, builder.Environment);

if (emitOpenApiTo is not null)
{
    builder.Services.RemoveAll<IHostedService>();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}

var app = builder.Build();

app.MapDefaultEndpoints();
app.LogCorsOrigins();
app.LogRealtimeBackplane();

// ---------------------------------------------------------------------
// 3. Ensure database schemas exist (creates tables when no migrations are applied yet)
// ---------------------------------------------------------------------

if (emitOpenApiTo is null)
{
    using var scope = app.Services.CreateScope();

    // Audit schema — independent of all module schemas.
    var auditDb = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
    await auditDb.Database.MigrateAsync();
    await SankoreDataProtection.InitializeAsync(scope.ServiceProvider);

    // Secrets vault — shared infrastructure, so no module owns its schema: it has to be
    await SecretsServiceCollectionExtensions.InitializeAsync(scope.ServiceProvider);

    // AdministrationModule.InitializeAsync runs migrations AND seeds system roles.
    await LeadsModule.InitializeAsync(scope.ServiceProvider);
    await AdministrationModule.InitializeAsync(scope.ServiceProvider);
    await WorkflowModule.InitializeAsync(scope.ServiceProvider);
    await NotificationsModule.InitializeAsync(scope.ServiceProvider);
    await CustomersModule.InitializeAsync(scope.ServiceProvider);
    await KycModule.InitializeAsync(scope.ServiceProvider);
    await IntegrationModule.InitializeAsync(scope.ServiceProvider);

    // AFTER the modules: it sends RegisterCommand, which assigns the System role, and that role
    // is seeded by AdministrationModule.InitializeAsync just above. It takes the root provider
    // and not this scope — it establishes an ambient tenant scope and needs to build its own.
    await SystemUserSeeder.SeedAsync(app.Services);
}

// ---------------------------------------------------------------------
// 4. HTTP pipeline
// ---------------------------------------------------------------------

// The Swagger UI and the OpenAPI document are a complete map of this API plus a client for it,
// so they are served only where SwaggerExposure allows (Development by default) and always behind
// HTTP Basic auth. The gate must come first: UseSwagger answers the request itself.
if (SwaggerExposure.IsEnabled(app.Configuration, app.Environment))
{
    app.UseSwaggerBasicAuth();
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (emitOpenApiTo is null) { 
    try {
        var tenantStore = app.Services.GetRequiredService<Sankore.Shared.Kernel.ITenantStore>();
        var activeTenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var pauseStore = new RecurringJobPauseStore(
            app.Services.GetRequiredService<Hangfire.JobStorage>(),
            app.Services.GetRequiredService<Hangfire.IRecurringJobManager>());

        var pausedJobIds = pauseStore.GetPausedIds();
        var skipped = new List<string>();

        void AddOrUpdate<TJob>(string jobId, Expression<Func<TJob, Task>> methodCall, string cron)
        {
            if (pausedJobIds.Contains(jobId))
            {
                skipped.Add(jobId);
                return;
            }

            Hangfire.RecurringJob.AddOrUpdate(jobId, methodCall, cron);
        }

        foreach (var t in activeTenants)
    {
        var tenantId = t.Id;
        var suffix = tenantId.ToString()[..8];

        AddOrUpdate<Sankore.Modules.Leads.Features.SlaMonitoring.CheckSlaBreachesJob>(
            $"sla-breach-check-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "*/15 * * * *");

        AddOrUpdate<Sankore.Modules.Leads.Features.NurturingExecution.ExecuteNurturingJob>(
            $"nurturing-execution-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "*/10 * * * *");

        AddOrUpdate<Sankore.Modules.Leads.Features.ReactivateRecycledLeads.ReactivateRecycledLeadsJob>(
            $"reactivate-recycled-leads-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "0 3 * * *");

        // Dispatch queue: leads no agent could take are retried under the dispatching rule until
        // one becomes available. Every two minutes — the wait an agent coming back from a break
        // sees before the queue reaches them — and nearly free when nobody is available.
        AddOrUpdate<Sankore.Modules.Leads.Features.DispatchQueue.DispatchWaitingLeadsJob>(
            $"dispatch-waiting-leads-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "*/2 * * * *");

        // Health questionnaire retention (BNF-B-09). Nightly, offset from the other jobs: it
        // reads the tenant's own retention period and blanks only answers already transmitted to
        // the insurer, so there is nothing to race with.
        AddOrUpdate<Sankore.Modules.Leads.Features.Questionnaires.PurgeHealthResponsesJob>(
            $"purge-health-responses-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "30 3 * * *");

        // Lead anonymisation (BNF-B-19). AFTER the health purge, deliberately: that one blanks
        // transmitted answers on leads of any age, this one destroys everything about leads closed
        // for the tenant's retention period. Running them the other way round would have the
        // anonymisation purge answers the health job is the right place to reason about.
        AddOrUpdate<Sankore.Modules.Leads.Features.Retention.AnonymizeClosedLeadsJob>(
            $"anonymize-closed-leads-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "45 3 * * *");

        // Contract-anniversary KYC updates (CIMA LBC/FT: « les informations sont mises à jour à la
        // date anniversaire du contrat »). A different rule from the periodic review above, with a
        // different clock: that one follows the vigilance level, this one the contract's date.
        //
        // 02:15, AFTER the 01:00 periodic review sweep, deliberately. A file that was already due
        // for a periodic review this morning has an open review by then, and the anniversary job
        // skips a file that has one — so a client does not get two tasks in one night for the two
        // rules landing on the same day. The other order would raise the anniversary review first
        // and let the periodic sweep add its own beside it.
        AddOrUpdate<Sankore.Modules.Kyc.Features.Reviews.ContractAnniversaryReviewJob>(
            $"kyc-contract-anniversary-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "15 2 * * *");

        // ASS-07: the insurers' own view of their policies, into the read model the
        // reconciliation run and the monthly statement both read. At 03:30, BEFORE INT-34's
        // reconciliation at 06:00 — deliberately: reconciling against a read model a day stale
        // would report every overnight change at the insurer as a gap to arbitrate.
        AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.PolicySync.SyncPoliciesJob>(
            $"insurance-policy-sync-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "30 3 * * *");

        // ASS-08: the premium schedule, then the debits that have fallen due. AFTER the policy
        // sync of the same night, deliberately — a policy discovered at 03:30 gets its schedule
        // at 03:45 rather than waiting a day, and a periodicity corrected at the insurer is
        // honoured by the same run that learned about it.
        AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Premiums.CollectPremiumsJob>(
            $"insurance-premium-collection-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "45 3 * * *");

        // ASS-09: the open dossiers at each insurer. At 04:00, after the policy sync — a claim
        // discovered on a policy the same night is then placed on a contract this platform
        // already knows, instead of being dropped for want of one.
        AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Claims.ClaimSync.SyncClaimsJob>(
            $"insurance-claim-sync-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "0 4 * * *");

        // ASS-10: the previous month's bordereau per insurer. MONTHLY, on the 1st at 05:00 —
        // after that night's policy sync and premium collection, so the last day of the month is
        // in the figures, and before INT-34's 06:00 reconciliation. It rebuilds its own draft, so
        // running it again on the 2nd is harmless; a month already finalised is refused.
        AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Statements.GenerateStatementsJob>(
            $"insurance-statement-generation-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "0 5 1 * *");

        // ASS-12: dates the insurance evidence, then destroys what has outlived its retention.
        // WEEKLY and not nightly, on purpose — a ten-year deadline does not need a daily sweep,
        // and the one job in the module whose mistake cannot be undone by running it again should
        // run as seldom as its job allows. Its destructive half is off unless
        // Integration:Insurance:Retention:PurgeEnabled says otherwise.
        AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Retention.PurgeInsuranceEvidenceJob>(
            $"insurance-evidence-retention-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "30 2 * * 0");

        // The règlement 01/CIMA/2024's complaints mechanism, chased before it is breached. Daily
        // at 07:00, deliberately in working hours and not with the nightly sweeps: it produces a
        // task for an agent who has until the end of the day to act on it.
        AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Complaints.ComplaintDeadlineJob>(
            $"insurance-complaint-deadlines-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "0 7 * * *");

        // ASS-09's antivirus pass, then the transmission of the pieces the insurer asked for.
        // EVERY TEN MINUTES and not nightly: an agent at a counter deposits a complement and the
        // insurer is waiting on it, so a dossier must not sit a day on a scan that takes a second.
        // Both passes are bounded per run, and a piece left unscanned by one sweep is picked up by
        // the next.
        AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Claims.Documents.ScanClaimDocumentsJob>(
            $"insurance-claim-document-scan-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "*/10 * * * *");
    }

        // Pull orchestrator — runs every minute across all tenants (F13.37-BE-21)
        AddOrUpdate<Sankore.Modules.Leads.Features.Ingestion.Pull.LeadSourcePullOrchestratorJob>(
            "lead-source-pull-orchestrator",
            job => job.ExecuteAsync(),
            "* * * * *");

        // ── Customers module (M01) ────────────────────────────────────────────────
        // All four are GLOBAL orchestrators: they iterate active tenants themselves and
        // enqueue one opaque per-tenant job each, so adding a tenant needs no new
        // recurring registration and the job arguments stay opaque identifiers.
        AddOrUpdate<Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates.DetectDuplicatesOrchestratorJob>(
            "customers-detect-duplicates-orchestrator",
            job => job.ExecuteAsync(),
            "0 2 * * *");                                    // nightly (US-M01-BE-24)

        AddOrUpdate<Sankore.Modules.Customers.Features.Timeline.Segments.AssignSegmentsOrchestratorJob>(
            "customers-assign-segments-orchestrator",
            job => job.ExecuteAsync(),
            "0 3 * * *");                                    // nightly (US-M01-BE-27)

        AddOrUpdate<Sankore.Modules.Customers.Features.Timeline.Loyalty.ComputeLoyaltyScoresOrchestratorJob>(
            "customers-loyalty-scores-orchestrator",
            job => job.ExecuteAsync(),
            "30 3 * * *");                                   // nightly (US-M01-BE-28)

        // ── KYC module (M02) ──────────────────────────────────────────────────────
        // GLOBAL like M01's: it walks the active tenants itself and enqueues one opaque per-tenant
        // job, so a new tenant needs no new recurring registration. 01:00 is before M01's 02:00 and
        // 03:00 sweeps — a review that downgrades a tier should land before the nightly scoring reads
        // it.
        AddOrUpdate<Sankore.Modules.Kyc.Features.Reviews.KycReviewOrchestratorJob>(
            "kyc-review-orchestrator",
            job => job.ExecuteAsync(),
            "0 1 * * *");                                    // daily (KYC-B-07)

        // Also GLOBAL, and deliberately AFTER the review sweep: it closes the remote-collection
        // links whose window has passed and tells the agent who converted the customer. The link
        // itself is already dead on the clock — the sweep is what makes it visible on the screen.
        AddOrUpdate<Sankore.Modules.Kyc.Features.RemoteInvitations.Expiry.KycRemoteInvitationExpiryOrchestratorJob>(
            Sankore.Modules.Kyc.Features.RemoteInvitations.Expiry
                .KycRemoteInvitationExpiryOrchestratorJob.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Kyc.Features.RemoteInvitations.Expiry
                .KycRemoteInvitationExpiryOrchestratorJob.CronExpression);   // daily (KRM-B-04)

        // KRM-B-09's relance. HOURLY and not daily, unlike its neighbours: the SLA is expressed in
        // hours and defaults to 24, so a daily sweep would turn that promise into anything between
        // 24 and 48 depending on what time of day the file was routed.
        AddOrUpdate<Sankore.Modules.Kyc.Features.Routing.ReviewSla.KycRemoteReviewSlaOrchestratorJob>(
            Sankore.Modules.Kyc.Features.Routing.ReviewSla
                .KycRemoteReviewSlaOrchestratorJob.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Kyc.Features.Routing.ReviewSla
                .KycRemoteReviewSlaOrchestratorJob.CronExpression);          // hourly (KRM-B-09)

        // ── Integration module ────────────────────────────────────────────────────
        // Both GLOBAL orchestrators: they walk the active tenants themselves and enqueue opaque
        // per-tenant work, so adding a tenant needs no new recurring registration.
        //
        // Every minute, like the lead-source puller. The sweep is cheap when there is nothing to
        // do — one indexed count per tenant on (status, next_attempt_at) — and the cost of being
        // slower is a customer waiting at a counter for a CBS write that is already queued.
        AddOrUpdate<Sankore.Modules.Integration.Features.Dispatch.IntegrationDispatchOrchestratorJob>(
            "integration-dispatch-orchestrator",
            job => job.ExecuteAsync(),
            "* * * * *");

        // 02:00 daily, and daily rather than monthly on purpose: the four statements are
        // CREATE TABLE IF NOT EXISTS ... PARTITION OF, so re-running is a no-op, while a monthly
        // schedule that misses its single slot loses the month — and an INSERT past the last
        // partition FAILS rather than landing somewhere. 02:00 is already the duplicate-detection
        // slot below; the two touch different schemas and neither holds a long lock.
        AddOrUpdate<Sankore.Modules.Integration.Infrastructure.CallLog.EnsureCallLogPartitionsJob>(
            "integration-call-log-partitions",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 2 * * *");

        // Même raisonnement et même créneau pour la table des événements de téléphonie : sans
        // partition au premier du mois, le journal des appels se tait à minuit et le fournisseur
        // continue de livrer des événements refusés un par un (TEL-B-03).
        AddOrUpdate<Sankore.Modules.Integration.Infrastructure.Telephony.EnsureTelephonyPartitionsJob>(
            "integration-telephony-partitions",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 2 * * *");

        // Les enregistrements (TEL-B-16, TEL-B-17). Deux planifications et non une : récupérer
        // court après le fournisseur tous les quarts d'heure, purger est un devoir de rétention
        // quotidien — et un opérateur qui met l'une en pause depuis la page « Job control » ne
        // doit pas mettre l'autre. Mettre la récupération en pause perd des enregistrements que
        // l'institution voulait ; mettre la purge en pause garde des enregistrements qu'elle est
        // obligée de détruire.
        //
        // Les deux ne font rien pour un tenant dont la politique est Disabled, ce qui est tout
        // tenant jusqu'à ce qu'un responsable conformité énonce une base légale.
        AddOrUpdate<Sankore.Modules.Integration.Features.Telephony.Recordings
            .FetchTelephonyRecordingsOrchestrator>(
            Sankore.Modules.Integration.Features.Telephony.Recordings
                .FetchTelephonyRecordingsOrchestrator.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.Telephony.Recordings
                .FetchTelephonyRecordingsOrchestrator.CronExpression);

        AddOrUpdate<Sankore.Modules.Integration.Features.Telephony.Recordings
            .PurgeTelephonyRecordingsOrchestrator>(
            Sankore.Modules.Integration.Features.Telephony.Recordings
                .PurgeTelephonyRecordingsOrchestrator.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.Telephony.Recordings
                .PurgeTelephonyRecordingsOrchestrator.CronExpression);

        // TEL-B-19 : les indicateurs, recalculés depuis le journal. Deux horaires pour un seul
        // job, et le second n'est pas redondant.
        //
        // L'horaire horaire (:20) couvre les deux jours que quelqu'un regarde, pour qu'un appel
        // manqué apparaisse dans l'heure sans faire payer un recalcul à chaque webhook. L'horaire
        // nocturne (04:10) couvre une semaine, parce que ce qui change un chiffre APRÈS coup n'est
        // pas un événement que ce module voit à temps : un superviseur qui corrige une
        // attribution, un client anonymisé, une issue dont l'indicateur « compte comme contact »
        // était faux depuis le début. 04:10 vient APRÈS l'anonymisation des leads de M13 à 03:45
        // et la purge des enregistrements à 03:15 — tous deux modifient des appels, et un recalcul
        // qui passerait avant recalculerait l'état qu'ils sont sur le point de laisser derrière.
        AddOrUpdate<Sankore.Modules.Integration.Features.Telephony.Stats
            .RecomputeTelephonyStatsHourlyOrchestrator>(
            Sankore.Modules.Integration.Features.Telephony.Stats
                .RecomputeTelephonyStatsHourlyOrchestrator.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.Telephony.Stats
                .RecomputeTelephonyStatsHourlyOrchestrator.CronExpression);

        AddOrUpdate<Sankore.Modules.Integration.Features.Telephony.Stats
            .RecomputeTelephonyStatsNightlyOrchestrator>(
            Sankore.Modules.Integration.Features.Telephony.Stats
                .RecomputeTelephonyStatsNightlyOrchestrator.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.Telephony.Stats
                .RecomputeTelephonyStatsNightlyOrchestrator.CronExpression);

        // 02:40 : le contrôle quotidien des liaisons utilisateur/fournisseur (TEL-B-05). Après le
        // job de partitions de 02:00, pour qu'un contrôle qui écrit écrive dans une partition qui
        // existe. Rien ne nous prévient qu'un compte a été retiré de l'annuaire du fournisseur —
        // c'est une modification faite dans la console d'une autre société — et le seul symptôme
        // visible autrement est l'historique d'un agent qui cesse de se remplir.
        AddOrUpdate<Sankore.Modules.Integration.Features.Telephony.UserLinks.CheckUserLinks
            .CheckTelephonyUserLinksOrchestrator>(
            Sankore.Modules.Integration.Features.Telephony.UserLinks.CheckUserLinks
                .CheckTelephonyUserLinksOrchestrator.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.Telephony.UserLinks.CheckUserLinks
                .CheckTelephonyUserLinksOrchestrator.CronExpression);

        // Every minute is the RESOLUTION of the schedule, not the sweep period: each (connection,
        // stream) pair carries its own interval in the connection's settings, and a slower cron
        // would silently round every configured interval up to itself.
        AddOrUpdate<Sankore.Modules.Integration.Features.Sync.IntegrationSyncOrchestrator>(
            Sankore.Modules.Integration.Features.Sync.IntegrationSyncOrchestrator.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.Sync.IntegrationSyncOrchestrator.CronExpression);

        // 05:00, after the night's other sweeps rather than alongside them: M02's periodic review
        // at 01:00 can move a tier and therefore whether a customer is capped at all, and the
        // integration sync is what puts the night's figures into the snapshot this watch reads.
        // Every five minutes (INT-24). A cut-off is a time of day PER CONNECTION, so a single
        // daily schedule is wrong twice: there is no one hour to schedule it at, and a failed
        // deposit or a due purge has to be retried through the day rather than waiting until
        // tomorrow. Five minutes is the worst-case lag between a cut-off and its file.
        AddOrUpdate<Sankore.Modules.Integration.Features.Batch.Outbound.OutboundBatchOrchestratorJob>(
            "integration-outbound-batch",
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.Batch.Outbound.OutboundBatchOrchestratorJob.CronExpression);

        // Every quarter hour (INT-25). A batch rhythm is daily, but the partner deposits at an
        // hour neither side controls: polling four times an hour bounds how late an acknowledgement
        // is noticed to fifteen minutes, at four SFTP sessions per hour rather than sixty, and the
        // overdue delay of criterion 3 is measured in hours anyway.
        AddOrUpdate<Sankore.Modules.Integration.Features.Batch.Inbound.InboundBatchPollOrchestratorJob>(
            "integration-inbound-batch-poll",
            job => job.ExecuteAsync(),
            "*/15 * * * *");

        AddOrUpdate<Sankore.Modules.Integration.Features.KycLimits.KycLimitWatchOrchestratorJob>(
            Sankore.Modules.Integration.Features.KycLimits.KycLimitWatchOrchestratorJob.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.KycLimits.KycLimitWatchOrchestratorJob.CronExpression);

        // 06:00, after every other nightly sweep including the ceiling watch at 05:00, which reads
        // the same two sides. A comparison run against state another sweep is still changing
        // reports divergences that were never real; run during opening hours it reports ones a
        // counter closes a minute later, which is how a compliance ledger stops being believed
        // (INT-34).
        AddOrUpdate<Sankore.Modules.Integration.Features.Reconciliation.ReconciliationOrchestratorJob>(
            Sankore.Modules.Integration.Features.Reconciliation.ReconciliationOrchestratorJob.RecurringJobId,
            job => job.ExecuteAsync(),
            Sankore.Modules.Integration.Features.Reconciliation.ReconciliationOrchestratorJob.CronExpression);

        AddOrUpdate<Sankore.Modules.Customers.Features.Compliance.Retention.IdentifyRetentionCandidatesOrchestratorJob>(
            "customers-retention-candidates-orchestrator",
            job => job.ExecuteAsync(),
            "0 4 1 * *");                                    // monthly (US-M01-BE-29)

        app.Logger.LogInformation(
            "Registered recurring Hangfire jobs for {TenantCount} active tenant(s), plus the global pull orchestrator.",
            activeTenants.Count);

        if (skipped.Count > 0) 
        {
            app.Logger.LogInformation(
                "Skipped {SkippedCount} recurring job(s) paused from the Hangfire dashboard: {SkippedJobIds}. "
                + "They will not run until resumed there.",
                skipped.Count,
                string.Join(", ", skipped)); 
        } 
    } 
    catch (Exception ex)
    {
        app.Logger.LogError(ex,
            "Failed to register recurring Hangfire jobs. The API will start WITHOUT them, so "
            + "SLA escalation, nurturing, lead recycling and scheduled pulls will not run on this "
            + "instance. This usually means another instance is already running against the same database."); 
    }
}

app.UseExceptionHandler();
app.UseHttpLogging();
app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
// Extract + verify tenant against the external store. The prefixes are the public surfaces
// mapped at the bottom of this file: their tenant comes from the URL — a lead-source public key,
// a connection id, a relay agent's client certificate — so there is no JWT and no recognised
// domain to resolve, and without them every one of those routes answers
// 400 "Cannot determine tenant" instead of reaching its endpoint. Keep this list and the
// app.Map*PublicEndpoints() calls below in step.
app.UseTenantResolution(
    "/api/ingest",                  // M13 web form + webhook ingest (public key in the path)
    "/sdk",                         // the forms.js builds those pages load
    "/integration/webhooks",        // M14 inbound sync hooks (connection id + HMAC)
    "/integration/relay-agents",    // M14 relay agent enrolment + heartbeat (client certificate)
    SankoreRealtimeHub.Path);       // le hub : le tenant vient du claim du jeton, pas du domaine
app.UseRequestLocalization(opts =>
{
    opts.SupportedCultures = [new("fr"), new("en")];
    opts.SupportedUICultures = [new("fr"), new("en")];
    opts.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("fr");
    opts.ApplyCurrentCultureToResponseHeaders = true;
});
app.UseLanguageResolution(); // resolve active language (JWT claim → Accept-Language → default fr) and set CurrentUICulture
app.UseCorrelationId();      // push CorrelationId + TenantId + UserId into log scope
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
    .WithTags("Health")
    .WithOpenApi()
    .AllowAnonymous();

// V1
var appVersion1 = app.MapGroup("api/v1").RequireRateLimiting("api");

appVersion1.MapAdministrationModuleEndpoints();
appVersion1.MapLeadsEndpoints();
appVersion1.MapWorkflowModuleEndpoints();
appVersion1.MapNotificationsModuleEndpoints();
appVersion1.MapCustomersModuleEndpoints();
appVersion1.MapKycModuleEndpoints();
appVersion1.MapIntegrationModuleEndpoints();

// The customer-facing remote-KYC surface (KRM-B-05/06/07). Under api/v1/public, which is what
// TenantResolutionMiddleware exempts — so the tenant is resolved from the token or the session and
// compared with X-Tenant-Fqdn inside KycRemoteGate instead. Its own rate-limiting policy, and
// NOT the "api" one: that policy partitions on the JWT subject, which an anonymous caller has
// none of, so every customer in the country would have shared one window.
appVersion1.MapGroup("public")
    .RequireRateLimiting("kyc-public")
    .MapKycPublicEndpoints();

// The SMS providers' delivery callbacks. Under `public` because TenantResolutionMiddleware
// exempts that prefix and nothing else, and a provider callback carries no tenant header — each
// endpoint resolves the tenant from its own payload and verifies it before writing anything.
appVersion1.MapGroup("public")
    .RequireRateLimiting("sms-webhooks")
    .MapNotificationsPublicEndpoints();

// The institution's logo (company branding). Under `public` for the same reason as the two groups
// above — a mail client opening an e-mail sends no tenant header — and on the inherited "api"
// policy, which falls back to the caller's IP for an anonymous request: browsers and mail proxies
// cache the response for a week, so a page view is not a request.
appVersion1.MapGroup("public")
    .MapAdministrationPublicEndpoints();

appVersion1.MapGroup("audit").MapGetAuditEntries();
appVersion1.MapGroup("object-storage").MapMigrateObjects();

app.MapBootstrapEndpoints();

// Public (unauthenticated) ingest endpoints — outside api/v1, own rate limiting
app.MapPublicIngestEndpoints();

// Integration webhooks (INT-20). Outside api/v1 and before authentication, like M13's ingest: the
// caller is an external back office with no JWT, and the HMAC signature over the body IS the
// authentication. An unknown or deactivated connection answers 401 exactly as a bad signature
// does — a 404 would turn the route into a connection-enumeration oracle.
app.MapIntegrationPublicEndpoints();

// Le hub temps réel (TEL-B-10). Avant la ligne qui émet l'OpenAPI, comme toute route — un hub ne
// contribue aucun chemin au document, donc le seul symptôme d'une erreur d'ordre ici serait
// l'absence de symptôme.
app.MapSankoreRealtimeHub();

// app.MapKycEndpoints();
// app.MapLoansEndpoints();

// After every Map* call: the document is built from the endpoint data sources, so a route mapped
// below this line would be missing from the generated client without anything failing.
if (emitOpenApiTo is not null)
{
    await OpenApiEmitter.WriteAsync(app, emitOpenApiTo);
    return;
}

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
