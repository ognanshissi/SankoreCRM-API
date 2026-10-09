namespace Sankore.Modules.Integration.Features.Sync;

using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The inbound webhook of the integration module (INT-20, criterion 4).
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>HOST WIRING REQUIRED — <c>src/Bootstrapper/Sankore.Api/Program.cs</c>.</b> This group is
/// mapped on the ROOT application, <b>outside <c>api/v1</c></b> and <b>before
/// <c>app.UseAuthentication()</c></b>, exactly where M13's public ingest endpoints are mapped:
/// </para>
/// <code>
/// app.MapIntegrationWebhookEndpoints();
/// </code>
/// <para>
/// Outside <c>api/v1</c> because the caller is a bank's back office, not the SANKORE front end: it
/// has no JWT and no version contract with us. Before <c>UseAuthentication</c> for the same reason
/// M13's are — an anonymous endpoint that sits behind the authentication middleware still pays for
/// a token it will never have, and a malformed <c>Authorization</c> header from a third-party
/// sender would fail the request before the signature is ever checked.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
///
/// <para>
/// <b>Everything about the answer is deliberately uninformative.</b> <c>204</c> when the request is
/// authentic, <c>401</c> otherwise — including for a connection that does not exist, one that has
/// been deactivated, and one with no signing secret configured. A <c>404</c> for an unknown id
/// would turn this endpoint into a tenant-enumeration oracle: anybody could walk connection ids and
/// learn which exist, which is an inventory of who our customers integrate with. The outcome type
/// has exactly two values for the same reason — there is no reason code anywhere to leak, so no
/// future edit can accidentally return one.
/// </para>
/// </summary>
internal static class SyncWebhookEndpoints
{
    /// <summary>
    /// A webhook names a customer; it is not a data feed. Sixty-four kilobytes is already two
    /// orders of magnitude more than the envelope needs, and an explicit ceiling is what keeps a
    /// public, unauthenticated endpoint from buffering a Kestrel-sized body per request before the
    /// signature has told us whether to care.
    /// </summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// The envelope's reader. Case-insensitive so that <c>externalId</c>, <c>ExternalId</c> and
    /// <c>EXTERNALID</c> are all read — a signature-valid request refused over the casing of a
    /// field name would be a support call nobody could diagnose from either end. No naming policy:
    /// one spelling of each field is documented (<c>externalId</c>, <c>entityType</c>), and
    /// accepting <c>external_id</c> as well would need a converter, which is a format negotiation
    /// no specification has asked for.
    /// </summary>
    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static IEndpointRouteBuilder MapIntegrationWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/integration/webhooks/{connectionId:guid}", HandleAsync)
            .AllowAnonymous()
            // The repo's existing public-ingest policy: a fixed window of 10 requests a minute,
            // partitioned by remote IP and the route's `publicKey`. This route has no `publicKey`,
            // so the partition degrades to one window per IP covering every integration webhook
            // from that sender — see SyncServiceRegistration for the consequence an operator has
            // to decide on. Reused rather than replaced because inventing a policy name the host
            // does not declare means no rate limiting at all, silently.
            .RequireRateLimiting("integration-webhook")
            .WithName("IntegrationWebhook")
            .WithTags("Integration")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        Guid connectionId,
        HttpContext http,
        IntegrationDbContext db,
        ISecretsModule secrets,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        IOptions<IntegrationWebhookOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(SyncWebhookEndpoints));

        var body = await ReadBodyAsync(http.Request.Body, ct);

        // A body over the ceiling is refused without being read to the end and therefore without
        // being verified, so the only honest answer is the same 401 as an unverifiable request.
        var outcome = body is null
            ? WebhookOutcome.Rejected
            : await AcceptAsync(
                connectionId,
                body,
                http.Request.Headers[SyncWebhookVerifier.TimestampHeader].ToString(),
                http.Request.Headers[SyncWebhookVerifier.SignatureHeader].ToString(),
                db, secrets, hangfire, clock, options.Value, logger, ct);

        return ToResult(outcome);
    }

    /// <summary>
    /// The only place an outcome becomes a status code: <c>204</c> or <c>401</c>, and in both cases
    /// an empty body. Split out so a test can pin it — the two codes and the absence of a payload
    /// are the whole of criterion 4's "no detail about which check failed".
    /// </summary>
    internal static IResult ToResult(WebhookOutcome outcome)
        => outcome == WebhookOutcome.Accepted
            ? Results.NoContent()
            : Results.Unauthorized();

    /// <summary>
    /// Everything the webhook decides, with no <c>HttpContext</c> in sight — so a test can pin the
    /// security properties without a <c>WebApplicationFactory</c>, which this repository has no
    /// precedent for.
    ///
    /// <para>
    /// Order of operations matters. The connection is resolved first because the signing secret is
    /// keyed by <c>(tenantId, connectionId)</c> and the tenant is not knowable from the request:
    /// there is no JWT here, so <c>IgnoreQueryFilters()</c> is mandatory and the tenant comes from
    /// the row. The signature is then verified <b>before the body is parsed</b> — parsing
    /// unverified remote input is how a webhook endpoint becomes a deserialisation surface.
    /// </para>
    ///
    /// <para>
    /// A verified request with a body we cannot act on — unparseable JSON, no external id, an
    /// entity type that is not a customer — is <b>accepted</b>. The signature already proved the
    /// sender, and a sender that is told 4xx will simply redeliver the same body on a schedule
    /// for ever. It is logged at warning level, which is where a field-name mismatch with a real
    /// installation will show up.
    /// </para>
    /// </summary>
    internal static async Task<WebhookOutcome> AcceptAsync(
        Guid connectionId,
        byte[] body,
        string? timestamp,
        string? signature,
        IntegrationDbContext db,
        ISecretsModule secrets,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        IntegrationWebhookOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(hangfire);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // No ambient tenant on a public endpoint: the query filter would compare against
        // Guid.Empty and find nothing. An inactive connection is treated as a non-existent one —
        // deactivating a connection must stop its webhook URL working, and answering differently
        // would say that the connection exists.
        var connection = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.Id == connectionId && c.IsActive)
            .Select(c => new { c.TenantId, c.Family })
            .FirstOrDefaultAsync(ct);

        if (connection is null)
        {
            logger.LogWarning(
                "Integration webhook refused for connection {ConnectionId}: unknown or inactive.",
                connectionId);

            return WebhookOutcome.Rejected;
        }

        var secret = await secrets.GetValueAsync(
            IntegrationSecrets.WebhookSecretKey(connection.TenantId, connectionId), ct);

        if (string.IsNullOrWhiteSpace(secret))
        {
            // Fail closed. A connection with no configured webhook secret cannot have its callers
            // authenticated, and "no secret configured" must never read as "no signature
            // required": that is an unauthenticated write path into the sync queue.
            logger.LogWarning(
                "Integration webhook refused for connection {ConnectionId} of tenant {TenantId}: "
                + "no webhook secret is configured.", connectionId, connection.TenantId);

            return WebhookOutcome.Rejected;
        }

        if (!SyncWebhookVerifier.Verify(
                secret, timestamp, signature, body, clock.GetUtcNow(), options.ReplayWindow))
        {
            logger.LogWarning(
                "Integration webhook refused for connection {ConnectionId} of tenant {TenantId}: "
                + "verification failed.", connectionId, connection.TenantId);

            return WebhookOutcome.Rejected;
        }

        var envelope = ReadEnvelope(body, logger, connectionId);

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.ExternalId))
        {
            logger.LogWarning(
                "Integration webhook for connection {ConnectionId} carried no usable external "
                + "identifier; accepted and ignored.", connectionId);

            return WebhookOutcome.Accepted;
        }

        // INT-20 criterion 4 is about the customer. An entity type we are not asked to act on is
        // acknowledged rather than refused, so a sender that emits several event families needs no
        // per-family configuration on its side to stop being told 401 by ours.
        if (!string.IsNullOrWhiteSpace(envelope.EntityType)
            && !string.Equals(
                envelope.EntityType, IntegrationEntityTypes.Customer, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Integration webhook for connection {ConnectionId} named entity type "
                + "{EntityType}; accepted and ignored.", connectionId, envelope.EntityType);

            return WebhookOutcome.Accepted;
        }

        // Hoisted: Hangfire serialises the argument expressions, and only these three values reach
        // the queue. No body, no header, no secret.
        var tenantId = connection.TenantId;
        var externalId = envelope.ExternalId!.Trim();

        hangfire.Enqueue<SyncCustomerJob>(job => job.ExecuteAsync(tenantId, connectionId, externalId));

        logger.LogInformation(
            "Integration webhook accepted for connection {ConnectionId} of tenant {TenantId}: "
            + "targeted customer sync enqueued.", connectionId, tenantId);

        return WebhookOutcome.Accepted;
    }

    /// <summary>
    /// The body, or <c>null</c> when it exceeds <see cref="MaxBodyBytes"/>.
    ///
    /// <para>
    /// Read as bytes and never as text: the HMAC is computed over exactly what arrived, and a
    /// decode-then-re-encode round trip through <c>StreamReader</c> is not byte-faithful for a body
    /// that is not valid UTF-8.
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

    private static IntegrationWebhookEnvelope? ReadEnvelope(
        byte[] body, ILogger logger, Guid connectionId)
    {
        try
        {
            return JsonSerializer.Deserialize<IntegrationWebhookEnvelope>(body, EnvelopeJson);
        }
        catch (JsonException ex)
        {
            // Logged with the exception's message only. The body itself is never logged: it is
            // remote input about one of the tenant's customers, and INT-08's rule that no payload
            // value reaches a log or an audit row does not stop applying because the payload came
            // in rather than out.
            logger.LogWarning(
                "Integration webhook for connection {ConnectionId} carried a body that is not "
                + "readable JSON ({Reason}); accepted and ignored.",
                connectionId, ex.Message);

            return null;
        }
    }
}

/// <summary>
/// The two answers this endpoint has. An enum rather than a result object carrying a reason,
/// because the promise is that the caller learns nothing about which check failed and the only way
/// to keep that promise through future edits is to have no reason to return.
/// </summary>
internal enum WebhookOutcome
{
    Accepted,
    Rejected
}

/// <summary>
/// Everything this module reads out of a webhook body: an identity, and nothing else.
///
/// <para>
/// <b>Deliberately two fields.</b> No specification defines what any particular core banking
/// system puts in its notifications, so a richer record would be a parser for a format nobody has
/// agreed — M02 has already paid for the version of that mistake where every hand-written field
/// name mapped to null. More importantly, the body is remote input and must not be a data source:
/// the targeted sync re-reads the customer from the external system through INT-21's projector, so
/// a sender cannot write values into the snapshot by describing them here. The webhook says
/// <i>who changed</i>; the external system remains the authority on <i>what</i>.
/// </para>
/// </summary>
/// <param name="EntityType">
/// Optional. Absent or empty means a customer, which is the only entity INT-20 acts on.
/// </param>
/// <param name="ExternalId">
/// The customer's identifier in the external system. Resolved against
/// <c>integration_reference</c> and never trusted beyond being a lookup key.
/// </param>
internal sealed record IntegrationWebhookEnvelope(string? EntityType, string? ExternalId);
