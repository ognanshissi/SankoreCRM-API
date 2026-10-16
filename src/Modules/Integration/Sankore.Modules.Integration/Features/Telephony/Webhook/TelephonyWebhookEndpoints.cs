namespace Sankore.Modules.Integration.Features.Telephony.Webhook;

using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The telephony provider's inbound webhook (TEL-B-07).
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>HOST WIRING REQUIRED — <c>src/Bootstrapper/Sankore.Api/Program.cs</c>.</b> Mapped on the
/// ROOT application, <b>outside <c>api/v1</c></b> and <b>before <c>app.UseAuthentication()</c></b>,
/// next to <c>MapIntegrationWebhookEndpoints()</c>, and the host must declare the named rate-limit
/// policy <c>telephony-webhook</c>: <b>a policy named here but not declared there limits nothing,
/// silently.</b> The <c>/api/v1/public</c> and <c>/integration/webhooks</c> prefixes are already
/// exempt from FQDN tenant resolution and this route sits under the second one.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
///
/// <para>
/// <b>Order of operations, and it is not negotiable:</b> resolve the connection, read its secret,
/// verify the signature, and only THEN hand the body to the adapter's parser. The tenant is not
/// knowable from the request — there is no JWT here — so the connection row is where it comes
/// from, which is why <c>IgnoreQueryFilters()</c> is mandatory. Parsing unverified remote input is
/// how a webhook endpoint becomes a deserialisation surface, and the parser is declared pure for
/// the same reason: anything it reached out to would be something an unverified caller can make it
/// reach out to.
/// </para>
///
/// <para>
/// <b>Two answers and no reason code</b>, exactly as the sync webhook: <c>204</c> or <c>401</c>,
/// both with an empty body. A <c>404</c> for an unknown connection id would make this endpoint a
/// tenant-enumeration oracle — an inventory of which institutions run telephony and with whom.
/// </para>
///
/// <para>
/// <b>What closes the replay hole the sync verifier documents.</b> <c>SyncWebhookVerifier</c> says
/// in its own remarks that it does not make a request single-use, because that needs a store of
/// seen nonces. <c>tel_webhook_event</c> IS that store: the unique index on the deduplication key
/// is the check, the insert is how it is performed, and a redelivery therefore does no work at all
/// — which matters far more here than it did there, because the action a telephony event triggers
/// is not an idempotent re-projection, it is a status transition.
/// </para>
/// </summary>
internal static class TelephonyWebhookEndpoints
{
    /// <summary>
    /// The named policy this route requires. A constant so the host and the route cannot drift —
    /// a mistyped policy name is accepted at start-up and limits nothing.
    /// </summary>
    internal const string RateLimitPolicy = "telephony-webhook";

    /// <summary>
    /// Thirty-two kilobytes. A call event is an identifier, a status and a pair of numbers; this
    /// is already two orders of magnitude more than that, and an explicit ceiling is what keeps an
    /// unauthenticated endpoint from buffering a Kestrel-sized body per request before the
    /// signature has told us whether to care.
    /// </summary>
    private const int MaxBodyBytes = 32 * 1024;

    internal static IEndpointRouteBuilder MapTelephonyWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/integration/telephony/webhooks/{connectionId:guid}", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("TelephonyWebhook")
            .WithTags("Telephony")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        Guid connectionId,
        HttpContext http,
        IntegrationDbContext db,
        IntegrationAdapterResolver resolver,
        TelephonyPayloadProtector payloads,
        ISecretsModule secrets,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        IOptions<IntegrationWebhookOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(TelephonyWebhookEndpoints));

        var body = await ReadBodyAsync(http.Request.Body, ct);

        // A body over the ceiling was not read to the end and therefore cannot be verified, so the
        // only honest answer is the same 401 as an unverifiable request.
        var outcome = body is null
            ? WebhookOutcome.Rejected
            : await AcceptAsync(
                connectionId,
                body,
                http.Request.Headers[SyncWebhookVerifier.TimestampHeader].ToString(),
                http.Request.Headers[SyncWebhookVerifier.SignatureHeader].ToString(),
                db, resolver, payloads, secrets, hangfire, clock, options.Value, logger, ct);

        return outcome == WebhookOutcome.Accepted
            ? Results.NoContent()
            : Results.Unauthorized();
    }

    /// <summary>
    /// Everything the endpoint decides, with no <c>HttpContext</c> in sight — so the security
    /// properties can be pinned by a test, which this repository has no <c>WebApplicationFactory</c>
    /// precedent for.
    /// </summary>
    internal static async Task<WebhookOutcome> AcceptAsync(
        Guid connectionId,
        byte[] body,
        string? timestamp,
        string? signature,
        IntegrationDbContext db,
        IntegrationAdapterResolver resolver,
        TelephonyPayloadProtector payloads,
        ISecretsModule secrets,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        IntegrationWebhookOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(hangfire);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // No ambient tenant on a public endpoint: the query filter would compare against
        // Guid.Empty and find nothing. The FAMILY is part of the predicate — a signature valid
        // for a core-banking connection must not open the telephony journal, and an operator who
        // pasted the wrong URL into a provider's console learns nothing from a 401 either way.
        var connection = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.Id == connectionId
                     && c.IsActive
                     && c.Family == IntegrationFamily.Telephony)
            .FirstOrDefaultAsync(ct);

        if (connection is null)
        {
            logger.LogWarning(
                "Telephony webhook refused for connection {ConnectionId}: unknown, inactive, or "
                + "not a telephony connection.", connectionId);

            return WebhookOutcome.Rejected;
        }

        var tenantId = connection.TenantId;

        var secret = await secrets.GetValueAsync(
            IntegrationSecrets.WebhookSecretKey(tenantId, connectionId), ct);

        if (string.IsNullOrWhiteSpace(secret))
        {
            // Fail closed. "No secret configured" must never read as "no signature required":
            // that is an unauthenticated write path into a tenant's call journal.
            logger.LogWarning(
                "Telephony webhook refused for connection {ConnectionId} of tenant {TenantId}: "
                + "no webhook secret is configured.", connectionId, tenantId);

            return WebhookOutcome.Rejected;
        }

        if (!SyncWebhookVerifier.Verify(
                secret, timestamp, signature, body, clock.GetUtcNow(), options.ReplayWindow))
        {
            logger.LogWarning(
                "Telephony webhook refused for connection {ConnectionId} of tenant {TenantId}: "
                + "verification failed.", connectionId, tenantId);

            return WebhookOutcome.Rejected;
        }

        // ── Verified. Only now is the body looked at. ────────────────────────
        var adapter = resolver.ResolveFor(connection);

        var port = adapter.IsSuccess
            ? resolver.ResolvePort<ITelephonyPort>(
                adapter.Value, IntegrationCapability.CallEvents)
            : IntegrationResult.Technical<ITelephonyPort>(adapter.Code!, adapter.Detail);

        if (port.IsFailure)
        {
            // Accepted, not refused: the sender is proven and a sender told 4xx redelivers the
            // same body on a schedule for ever. The deployment is missing an adapter or the
            // provider does not claim CallEvents — an operator's problem, not the caller's.
            logger.LogWarning(
                "Telephony webhook for connection {ConnectionId} of tenant {TenantId} cannot be "
                + "parsed ({Code}): no usable telephony adapter.",
                connectionId, tenantId, port.Code);

            return WebhookOutcome.Accepted;
        }

        var parsed = Parse(port.Value, body, logger, connectionId);

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.DedupKey))
        {
            logger.LogWarning(
                "Telephony webhook for connection {ConnectionId} carried no usable deduplication "
                + "key; accepted and ignored.", connectionId);

            return WebhookOutcome.Accepted;
        }

        var receivedAt = clock.GetUtcNow();
        var dedupKey = parsed.DedupKey.Trim();

        // The friendly answer, not the guarantee — the pattern ActivateConnectionHandler settled
        // on. The unique index below is what makes two concurrent deliveries safe; this read is
        // what makes the ordinary redelivery cheap, and it is also the only thing that works on
        // the EF InMemory provider the suites run against, which enforces no unique index at all.
        var alreadySeen = await db.TelWebhookEvents
            .IgnoreQueryFilters()
            .AnyAsync(
                e => e.TenantId == tenantId
                  && e.ConnectionId == connectionId
                  && e.DedupKey == dedupKey,
                ct);

        if (alreadySeen)
        {
            logger.LogInformation(
                "Telephony webhook for connection {ConnectionId} is a redelivery; no work "
                + "enqueued.", connectionId);

            return WebhookOutcome.Accepted;
        }

        var stored = TelWebhookEvent.Receive(
            tenantId, connectionId, dedupKey, parsed.EventType,
            payloads.Protect(body), receivedAt);

        // An event naming no call is stored and FINISHED here, not queued. There is nothing to
        // re-read, so a job could only reach the same conclusion after a round trip through the
        // queue — and an event left unprocessed with nothing coming for it makes "what is still
        // pending" unreadable. The row keeps the body for an operator either way: a provider
        // emitting events this module cannot act on is exactly what somebody has to diagnose.
        if (parsed.CallId is null)
        {
            stored.MarkProcessed("SKIPPED:NO_CALL_ID", receivedAt);

            db.TelWebhookEvents.Add(stored);
            await SaveAcceptingRedeliveryAsync(db, logger, connectionId, ct);

            logger.LogWarning(
                "Telephony webhook for connection {ConnectionId} named no call ({EventType}); "
                + "stored and ignored.", connectionId, parsed.EventType);

            return WebhookOutcome.Accepted;
        }

        db.TelWebhookEvents.Add(stored);

        if (!await SaveAcceptingRedeliveryAsync(db, logger, connectionId, ct))
            return WebhookOutcome.Accepted;

        // Hoisted: Hangfire serialises the argument expressions, and only these four values reach
        // the queue. No body, no header, no secret, no number.
        var eventId = stored.Id;

        hangfire.Enqueue<ProcessTelephonyWebhookJob>(
            job => job.ExecuteAsync(tenantId, connectionId, eventId, receivedAt));

        logger.LogInformation(
            "Telephony webhook accepted for connection {ConnectionId} of tenant {TenantId}: "
            + "{EventType} queued.", connectionId, tenantId, parsed.EventType);

        return WebhookOutcome.Accepted;
    }

    /// <summary>
    /// The adapter's parser, behind a catch it is contractually not supposed to need.
    ///
    /// <para>
    /// <c>ITelephonyPort.ParseWebhook</c> is declared not to throw and the contract suite pins
    /// that, including on a body that is not a payload at all. The guard is here anyway because
    /// the one caller is anonymous: a parser that threw would turn an unverified-shaped body into
    /// a 500, and 500s are a way to make an endpoint spend work.
    /// </para>
    /// </summary>
    private static ProviderWebhookEvent? Parse(
        ITelephonyPort port, byte[] body, ILogger logger, Guid connectionId)
    {
        try
        {
            return port.ParseWebhook(new TelephonyWebhookRequest(
                System.Text.Encoding.UTF8.GetString(body),
                new Dictionary<string, string>()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception's message only. The body is never logged: it carries both numbers of
            // a call, and §9 forbids a number reaching a log at all.
            logger.LogWarning(
                "Telephony webhook parser threw for connection {ConnectionId} ({Reason}), which "
                + "its contract forbids; accepted and ignored.", connectionId, ex.GetType().Name);

            return null;
        }
    }

    /// <summary>
    /// Saves, and answers false when the insert lost a race to a concurrent delivery of the same
    /// event.
    ///
    /// <para>
    /// The index is the guarantee the read above cannot be: two deliveries arriving in the same
    /// instant both pass the read, and the one that loses here must do no work rather than
    /// journalling the same transition twice. Matched by index NAME so only this constraint is
    /// swallowed — any other violation is a bug and must keep propagating.
    /// </para>
    /// </summary>
    private static async Task<bool> SaveAcceptingRedeliveryAsync(
        IntegrationDbContext db, ILogger logger, Guid connectionId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);

            return true;
        }
        catch (DbUpdateException ex) when (IsDedupViolation(ex))
        {
            // Information rather than warning: a provider redelivering is normal operation.
            logger.LogInformation(
                "Telephony webhook for connection {ConnectionId} lost the race to a concurrent "
                + "delivery of the same event; no work enqueued.", connectionId);

            return false;
        }
    }

    private static bool IsDedupViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains(
               "ux_tel_webhook_event_dedup", StringComparison.OrdinalIgnoreCase) == true
           || ex.Message.Contains("ux_tel_webhook_event_dedup", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The body, or <c>null</c> when it exceeds <see cref="MaxBodyBytes"/>.
    ///
    /// <para>
    /// Read as bytes and never as text: the HMAC is computed over exactly what arrived, and a
    /// decode-then-re-encode round trip is not byte-faithful for a body that is not valid UTF-8.
    /// </para>
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();

        var chunk = new byte[8 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }
}
