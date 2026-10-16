namespace Sankore.Modules.Integration.Features.Telephony.Webhook;

using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Posts a webhook to this tenant's own telephony connection, as the provider would (TEL-B-18,
/// and §9's end-to-end walkthrough).
///
/// <para>
/// <b>Why it exists.</b> There is no Ringover account in this deployment, so the only way to see a
/// call appear in the journal, get attached to a lead and reach an agent's screen is to make one
/// up. The alternative — a test-only code path inside the real endpoint — is the arrangement that
/// ships to production and gets found later.
/// </para>
///
/// <para>
/// <b>It goes through the REAL endpoint's decision function.</b> It signs the body with the
/// connection's own stored webhook secret and calls <c>AcceptAsync</c>, so a simulated event is
/// verified, deduplicated and queued exactly like a real one. A simulator that bypassed the
/// signature would be a simulator of everything except the part that goes wrong.
/// </para>
///
/// <para>
/// <b>Mapped in Development only, and it answers 404 elsewhere even if somebody maps it.</b> Two
/// guards rather than one: the mapping is the intent, the runtime check is what survives a
/// refactor of the mapping. The precedent is <c>AddFakeAdapter</c>, which throws outside
/// Development for the same reason.
/// </para>
///
/// <para>
/// The body is the adapter's own simulator shape — <c>"&lt;eventType&gt;:&lt;providerCallId&gt;"</c>
/// for the fake adapter. It is passed through untouched: this endpoint composes no payload, so it
/// cannot drift from whatever shape the adapter being exercised actually parses.
/// </para>
/// </summary>
internal static class SimulateTelephonyWebhookEndpoint
{
    internal static IEndpointRouteBuilder MapSimulateTelephonyWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("webhooks/simulate", Handle)
            .WithName("SimulateTelephonyWebhook")
            .WithSummary("DEVELOPMENT ONLY — post a webhook to your own telephony connection")
            .WithDescription(
                "Signs the body you give with this connection's stored webhook secret and runs "
                + "the real webhook's decision path: verification, deduplication, and the job "
                + "that re-reads the call from the provider and journals it. The body is passed "
                + "through untouched and must be whatever the tenant's adapter parses — the fake "
                + "adapter takes '<eventType>:<providerCallId>', e.g. 'ringing:fake-call-1'. "
                + "Posting the same body twice is deduplicated and does no second piece of work, "
                + "which is the property worth checking. Answers 404 outside Development and 422 "
                + "TELEPHONY_DISABLED without an active telephony connection. "
                + "Requires permission: Telephony.Settings.Manage.")
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces<SimulateTelephonyWebhookResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        SimulateTelephonyWebhookRequest request,
        IHostEnvironment environment,
        IntegrationDbContext db,
        IntegrationAdapterResolver resolver,
        TelephonyAccess access,
        TelephonyPayloadProtector payloads,
        ISecretsModule secrets,
        IBackgroundJobClient hangfire,
        ICurrentUser currentUser,
        TimeProvider clock,
        IOptions<IntegrationWebhookOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);

        // The runtime half of the guard. 404 and not 403: outside Development this route does not
        // exist, and saying "forbidden" would confirm that it does somewhere.
        if (!environment.IsDevelopment())
            return Results.NotFound();

        var connection = await access.ResolveAsync(currentUser.TenantId, ct);

        if (connection.IsFailure)
            return Results.UnprocessableEntity(new { error = connection.Code });

        var connectionId = connection.Value.Connection.Id;

        var secret = await secrets.GetValueAsync(
            IntegrationSecrets.WebhookSecretKey(currentUser.TenantId, connectionId), ct);

        if (string.IsNullOrWhiteSpace(secret))
        {
            // The real endpoint fails closed on a missing secret, so simulating one would only
            // ever produce a rejection. Said plainly instead, naming the route that fixes it.
            return Results.UnprocessableEntity(new
            {
                error = "INTEGRATION_WEBHOOK_SECRET_MISSING",
                detail = "Set it first: PUT integration/connections/{connectionId}/secrets.",
            });
        }

        var body = System.Text.Encoding.UTF8.GetBytes(request.Body ?? string.Empty);

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var outcome = await TelephonyWebhookEndpoints.AcceptAsync(
            connectionId,
            body,
            timestamp,
            SyncWebhookVerifier.Sign(secret, timestamp, body),
            db, resolver, payloads, secrets, hangfire, clock, options.Value,
            loggerFactory.CreateLogger(typeof(SimulateTelephonyWebhookEndpoint)),
            ct);

        return Results.Ok(new SimulateTelephonyWebhookResponse(
            connectionId, outcome == WebhookOutcome.Accepted));
    }
}

/// <summary>
/// <paramref name="Body"/> is passed to the adapter's parser verbatim — this endpoint composes
/// nothing, so it cannot drift from the shape the adapter reads.
/// </summary>
internal sealed record SimulateTelephonyWebhookRequest(string? Body);

/// <summary>
/// <paramref name="Accepted"/> is the real endpoint's own answer, so a <c>false</c> here is a
/// signature or parsing failure worth looking at rather than a quirk of the simulator.
/// </summary>
internal sealed record SimulateTelephonyWebhookResponse(Guid ConnectionId, bool Accepted);
