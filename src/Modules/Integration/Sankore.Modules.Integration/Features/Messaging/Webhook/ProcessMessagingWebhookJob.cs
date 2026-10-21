namespace Sankore.Modules.Integration.Features.Messaging.Webhook;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Turns one stored WhatsApp delivery into conversation messages and statuses (WA-BE-07 →
/// WA-BE-10) — a thin job: it opens the body, has the adapter read it, and hands the result to M13,
/// which owns the conversation and does the work.
///
/// <para>
/// <b>Its arguments are three identifiers.</b> The body, the numbers and the text stay in the
/// encrypted row; a Hangfire argument is a row in the shared database and a line on a dashboard.
/// </para>
///
/// <para>
/// <b>Retried for one reason of its own.</b> A delivery status can overtake the save of the
/// message it describes — Meta reports « sent » within a second of accepting, while the send's
/// answer is still being written. M13 reports such statuses as unmatched, and a RECENT delivery is
/// then thrown back to Hangfire, whose retries come seconds later; the messages it carried were
/// recorded the first time and are recognised by their id on the next pass. An old delivery with
/// unmatched statuses is finished as it is: a status for a message this platform never sent is
/// not going to start matching.
/// </para>
/// </summary>
public sealed class ProcessMessagingWebhookJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>How long an unmatched status is worth retrying for.</summary>
    internal static readonly TimeSpan UnmatchedRetryWindow = TimeSpan.FromMinutes(10);

    public async Task ExecuteAsync(Guid tenantId, Guid connectionId, Guid eventId)
    {
        // Set BEFORE the scope: ITenantContext and ICurrentUser are built from it. SYSTEM — a
        // provider sent this, not a person.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;

        await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<MessagingChannels>(),
            sp.GetRequiredService<TelephonyPayloadProtector>(),
            sp.GetRequiredService<ILeadsModule>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ProcessMessagingWebhookJob>>(),
            tenantId,
            connectionId,
            eventId,
            CancellationToken.None);
    }

    internal static async Task RunAsync(
        IntegrationDbContext db,
        MessagingChannels channels,
        TelephonyPayloadProtector payloads,
        ILeadsModule leads,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid connectionId,
        Guid eventId,
        CancellationToken ct)
    {
        var stored = await db.MsgWebhookEvents
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == eventId, ct);

        if (stored is null)
        {
            logger.LogWarning("WhatsApp webhook event {EventId} of tenant {TenantId} is no longer stored.", eventId, tenantId);
            return;
        }

        if (stored.ProcessedAt is not null) return;

        stored.RecordAttempt();

        var connection = await channels.GetAsync(tenantId, connectionId, ct);

        if (connection is null)
        {
            await FinishAsync(db, stored, "SKIPPED:CONNECTION_GONE", clock, ct);
            return;
        }

        var port = channels.PortFor(connection, IntegrationCapability.MessageEvents);

        if (port.IsFailure)
        {
            await FinishAsync(db, stored, $"SKIPPED:{port.Code}", clock, ct);
            return;
        }

        var body = payloads.Reveal(stored.PayloadEnc);

        if (body is null)
        {
            await FinishAsync(db, stored, "SKIPPED:UNREADABLE", clock, ct);
            return;
        }

        ConversationalWebhookBatch parsed;

        try
        {
            parsed = port.Value.ParseWebhook(System.Text.Encoding.UTF8.GetString(body));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The contract forbids throwing. The exception TYPE is logged, the body is not.
            logger.LogWarning(
                "WhatsApp webhook event {EventId}: the parser threw {Reason}, which its contract forbids.",
                eventId, ex.GetType().Name);

            await FinishAsync(db, stored, "SKIPPED:PARSE_FAILED", clock, ct);
            return;
        }

        // The door verified ONE number; every entry must be about it. An entry naming another
        // number is refused rather than filed here, where it would land in this tenant's records.
        var address = connection.ChannelAddress;

        var messages = parsed.Messages
            .Where(m => m.PhoneNumberId == address)
            .Select(m => new InboundMessagingMessage(
                m.ProviderMessageId, m.From, m.ProfileName, m.Timestamp, m.Type, m.Text,
                m.MediaId, m.MediaMimeType, m.MediaFileName, m.ReplyToProviderMessageId))
            .ToList();

        var statuses = parsed.Statuses
            .Where(s => s.PhoneNumberId == address)
            .Select(s => new MessagingDeliveryStatus(
                s.ProviderMessageId,
                s.Status,
                s.Timestamp,
                s.Status == "failed" ? CloudApiErrors.TranslateStatusError(s.ErrorCode) : null,
                s.ErrorTitle))
            .ToList();

        var foreign = parsed.Messages.Count + parsed.Statuses.Count - messages.Count - statuses.Count;

        if (foreign > 0)
        {
            logger.LogWarning(
                "WhatsApp webhook event {EventId}: {Count} entries named another number and were dropped.",
                eventId, foreign);
        }

        if (messages.Count == 0 && statuses.Count == 0)
        {
            await FinishAsync(db, stored, "EMPTY", clock, ct);
            return;
        }

        var outcome = await leads.RecordMessagingEventsAsync(
            tenantId, connectionId, MessagingChannels.AgencyOf(connection),
            new MessagingEventBatch(messages, statuses), ct);

        if (outcome.StatusesUnmatched > 0 && clock.GetUtcNow() - stored.ReceivedAt < UnmatchedRetryWindow)
        {
            // Saved first so the attempt count survives; then thrown so Hangfire comes back. The
            // messages are recognised by their id next time, so nothing is recorded twice.
            await db.SaveChangesAsync(ct);

            throw new InvalidOperationException(
                $"WhatsApp webhook event {eventId}: {outcome.StatusesUnmatched} status(es) name a message "
                + "not recorded yet; Hangfire will retry.");
        }

        await FinishAsync(
            db,
            stored,
            $"MESSAGES:{outcome.MessagesRecorded}/DUP:{outcome.MessagesDuplicate}/STATUSES:{outcome.StatusesApplied}/UNMATCHED:{outcome.StatusesUnmatched}",
            clock,
            ct);

        logger.LogInformation(
            "WhatsApp webhook event {EventId} of tenant {TenantId}: {Messages} message(s), {Statuses} status(es).",
            eventId, tenantId, outcome.MessagesRecorded, outcome.StatusesApplied);
    }

    private static async Task FinishAsync(
        IntegrationDbContext db, MsgWebhookEvent stored, string outcome, TimeProvider clock, CancellationToken ct)
    {
        stored.MarkProcessed(outcome, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }
}
