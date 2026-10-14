namespace Sankore.Modules.Notifications.Features.SmsWebhooks;

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Where the providers report what happened to a message after this platform handed it over.
///
/// <para>
/// <b>Why these exist at all.</b> Without them, <c>SmsOutboxStatus.Sent</c> is the end of the
/// story and it means "the provider accepted it" — not "the customer received it". On a channel
/// carrying one-time codes, the gap between those two is the entire support conversation: a
/// customer saying "I got nothing" and a platform insisting it sent something. A delivery callback
/// is what settles it.
/// </para>
///
/// <para>
/// <b>Anonymous, like every webhook, which is why verification is the first thing each does.</b>
/// Twilio signs its callbacks with the account's own Auth Token; Brevo does not sign SMS events at
/// all and carries a derived per-tenant token in its URL instead. See
/// <see cref="SmsWebhookSignatures"/> for why those two answers differ.
/// </para>
///
/// <para>
/// <b>A callback never moves a message back out of <c>Sent</c>.</b> It records what the provider
/// said beside the send, and nothing more: rewriting the status would make the outbox processor
/// pick the row up again and send a second message — a customer receiving two codes because the
/// first was reported undelivered.
/// </para>
/// </summary>
internal static class SmsWebhooksEndpoints
{
    /// <summary>
    /// Mounted under <c>public</c> by the host, beside the KYC journey, so
    /// <c>TenantResolutionMiddleware</c>'s exemption covers it: a provider's callback carries no
    /// tenant header and never will. Each endpoint resolves the tenant from its own payload.
    /// </summary>
    internal static IEndpointRouteBuilder MapSmsWebhooksEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("sms-webhooks").WithTags("SMS webhooks");

        group.MapPost("twilio", TwilioStatus)
            .WithName("ReceiveTwilioSmsStatus")
            .WithSummary("Twilio message status callback")
            .WithDescription(
                "Anonymous and verified on X-Twilio-Signature, which is an HMAC-SHA1 over the "
                + "request URL and the sorted form body keyed on the account's Auth Token. The "
                + "tenant is resolved from the AccountSid the payload carries, and the token is "
                + "then read from the vault to check the signature — so a callback for an account "
                + "this platform does not know is refused before anything is looked up. Always "
                + "answers 204, including for a message it cannot place: a provider that receives "
                + "an error retries for hours.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        group.MapPost("brevo", BrevoEvent)
            .WithName("ReceiveBrevoSmsEvent")
            .WithSummary("Brevo transactional SMS event")
            .WithDescription(
                "Anonymous. Brevo does not sign its SMS events, so the URL carries the tenant id "
                + "and a token derived from the platform key; both are required and the token is "
                + "compared in fixed time. The settings screen shows the exact URL to paste into "
                + "Brevo. Always answers 204, for the reason the Twilio callback does.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    // ── Twilio ──────────────────────────────────────────────────────────────

    private static async Task<IResult> TwilioStatus(
        HttpRequest request,
        NotificationsDbContext db,
        IAdministrationModule administration,
        ISecretsModule secrets,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(SmsWebhooksEndpoints));

        if (!request.HasFormContentType)
            return Results.NoContent();

        var form = await request.ReadFormAsync(ct);

        var accountSid = form["AccountSid"].ToString();
        var messageSid = form["MessageSid"].ToString();
        var status = form["MessageStatus"].ToString();
        var errorCode = form["ErrorCode"].ToString();

        if (string.IsNullOrWhiteSpace(accountSid) || string.IsNullOrWhiteSpace(messageSid))
            return Results.NoContent();

        // The tenant comes from the Account SID, the only tenant-identifying value a Twilio
        // callback carries. Through M12's contract and NOT a query against its table: a
        // cross-schema read is something this solution does not do even for a single column, and
        // only M12 knows which of its rows holds that SID.
        var resolved = await administration.ResolveTenantBySmsAccountSidAsync(accountSid, ct);

        if (resolved is not { } tenantId || tenantId == Guid.Empty)
        {
            // Not an error to report: a callback for an account nobody configured here is either
            // a stale Twilio setting or somebody else's traffic, and answering 403 would make
            // Twilio retry it for hours.
            logger.LogWarning(
                "Twilio SMS callback for an unknown account {AccountSid}; ignored", accountSid);

            return Results.NoContent();
        }

        var authToken = await secrets.GetValueAsync(
            NotificationSecrets.SmsCredentialKey(tenantId, "Twilio"), ct);

        var signatureUrl = ExternalUrl(request);

        var valid = SmsWebhookSignatures.IsValidTwilioSignature(
            signatureUrl,
            form.SelectMany(f => f.Value.Select(v => new KeyValuePair<string, string>(f.Key, v ?? string.Empty))),
            authToken ?? string.Empty,
            request.Headers["X-Twilio-Signature"].ToString());

        if (!valid)
        {
            // 403 here and NOT 204, unlike the unknown-account case: a signature mismatch is
            // either an attempt to forge a delivery record or a misconfigured proxy, and both
            // need to be visible. The URL is logged because the forwarded-header reconstruction
            // is the usual cause and the log line is what makes it diagnosable.
            logger.LogWarning(
                "Twilio SMS callback signature mismatch for tenant {TenantId} on {Url}",
                tenantId, signatureUrl);

            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        await RecordAsync(
            db, tenantId, messageSid, status,
            error: string.IsNullOrWhiteSpace(errorCode) ? null : $"TWILIO_{errorCode}",
            rawPayload: JsonSerializer.Serialize(
                form.ToDictionary(f => f.Key, f => f.Value.ToString())),
            logger, ct);

        return Results.NoContent();
    }

    // ── Brevo ───────────────────────────────────────────────────────────────

    private static async Task<IResult> BrevoEvent(
        [FromQuery] Guid? tenant,
        [FromQuery] string? token,
        BrevoSmsEventPayload? payload,
        NotificationsDbContext db,
        SmsWebhookSignatures signatures,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(SmsWebhooksEndpoints));

        if (tenant is not { } tenantId || tenantId == Guid.Empty)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (!signatures.IsValidBrevoToken(tenantId, token))
        {
            logger.LogWarning(
                "Brevo SMS callback presented an invalid token for tenant {TenantId}", tenantId);

            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (payload is null) return Results.NoContent();

        // Brevo correlates on the tag this platform set at send time — the message id — and
        // falls back to its own reference. The tag is preferred because it is OURS: a reference
        // that changed shape between provider versions would otherwise silently stop matching.
        var messageId = Guid.TryParse(payload.Tag, out var tagged) ? tagged : (Guid?)null;

        await RecordAsync(
            db, tenantId,
            providerMessageId: payload.MessageId,
            status: payload.Event,
            error: payload.Reason,
            rawPayload: JsonSerializer.Serialize(payload),
            logger, ct,
            outboxMessageId: messageId);

        return Results.NoContent();
    }

    // ── Shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the provider's word onto the message and into the delivery log.
    ///
    /// <para>
    /// A callback this platform cannot place still produces a LOG ROW, with a null message id. A
    /// webhook nobody can correlate is either a misconfiguration or somebody else's traffic, and
    /// dropping it silently is how both stay invisible.
    /// </para>
    /// </summary>
    private static async Task RecordAsync(
        NotificationsDbContext db,
        Guid tenantId,
        string? providerMessageId,
        string? status,
        string? error,
        string rawPayload,
        ILogger logger,
        CancellationToken ct,
        Guid? outboxMessageId = null)
    {
        var message = outboxMessageId is { } known
            ? await db.SmsOutboxMessages
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == known, ct)
            : await db.SmsOutboxMessages
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(
                    m => m.TenantId == tenantId && m.ProviderMessageId == providerMessageId, ct);

        var eventType = Classify(status);

        if (message is not null)
        {
            // Recorded BESIDE the send status, never over it — see the class remarks.
            message.RecordProviderStatus(status ?? "unknown", error);
        }

        db.SmsDeliveryLogs.Add(SmsDeliveryLog.Record(
            tenantId,
            message?.Id,
            eventType,
            // The row already holds the masked number by the time any callback arrives: the
            // processor redacts on every terminal state.
            message?.RecipientPhone ?? "••",
            rawPayload,
            status));

        await db.SaveChangesAsync(ct);

        if (message is null)
        {
            logger.LogWarning(
                "SMS delivery callback for tenant {TenantId} could not be matched to a message "
                + "(provider id {ProviderId}); logged uncorrelated",
                tenantId, providerMessageId);
        }
    }

    /// <summary>
    /// The providers' vocabularies folded onto this module's four events.
    ///
    /// <para>
    /// <b>An unrecognised word is <c>Accepted</c> and not <c>Undelivered</c>.</b> Both providers
    /// emit intermediate states — <c>queued</c>, <c>sending</c>, <c>accepted</c> — and a future
    /// one will emit a word this list does not know; reading that as a failure would report
    /// non-delivery for messages still in flight, which is the direction that produces false
    /// support tickets rather than missed ones. A genuine failure is always one of the words
    /// below.
    /// </para>
    /// </summary>
    private static SmsDeliveryEventType Classify(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "delivered" or "delivery" or "delivered_to_handset" => SmsDeliveryEventType.Delivered,

            "undelivered" or "failed" or "soft_bounce" or "hard_bounce" or "blocked"
                or "rejected" or "error" => SmsDeliveryEventType.Undelivered,

            _ => SmsDeliveryEventType.Accepted,
        };

    /// <summary>
    /// The URL as the PROVIDER built it, which behind a reverse proxy is not the one this
    /// application sees: TLS terminates upstream, so <c>Request.Scheme</c> is http and the host
    /// may be an internal name. Twilio's signature covers the external URL, so a signature
    /// computed over the internal one never matches — and the symptom is every callback reported
    /// as forged on a correct integration.
    /// </summary>
    private static string ExternalUrl(HttpRequest request)
    {
        var scheme = request.Headers["X-Forwarded-Proto"].ToString() is { Length: > 0 } forwarded
            ? forwarded.Split(',')[0].Trim()
            : request.Scheme;

        var host = request.Headers["X-Forwarded-Host"].ToString() is { Length: > 0 } forwardedHost
            ? forwardedHost.Split(',')[0].Trim()
            : request.Host.Value;

        return $"{scheme}://{host}{request.PathBase}{request.Path}{request.QueryString}";
    }
}

/// <summary>
/// Brevo's transactional SMS event, in the fields this platform reads. Brevo sends more; the rest
/// is kept in the raw payload on the log row, where a dispute is settled.
/// </summary>
/// <param name="Tag">
/// What this platform set at send time — the outbox message id. Preferred over
/// <paramref name="MessageId"/> for correlation because it is ours: a provider reference that
/// changes shape between versions silently stops matching.
/// </param>
public sealed record BrevoSmsEventPayload(
    string? Event,
    string? MessageId,
    string? Tag,
    string? Reason,
    string? To);
