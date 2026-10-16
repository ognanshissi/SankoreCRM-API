namespace Sankore.Modules.Integration.Features.Telephony.Webhook;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Turns one received webhook event into a journal entry (TEL-B-07, TEL-B-08).
///
/// <para>
/// <b>It re-reads the call from the provider and does not trust the body.</b> The event said
/// something happened; the API says what. That is the rule <c>TelCall</c> is built on, and it is
/// also what makes the webhook path safe: nothing a sender writes in a payload reaches a column,
/// so a signature-valid sender cannot describe a call into existence with a status and numbers of
/// its choosing. The stored body is kept encrypted for an operator to diagnose from, and is read
/// by nothing.
/// </para>
///
/// <para>
/// <b>On the <c>integration-sync</c> queue</b>, with the sweeps rather than with the default
/// workers: the two touch the same rows and the same provider's per-minute ceiling, and an
/// operator draining one has to be able to drain the other with it.
/// </para>
///
/// <para>
/// <b>Latency, named rather than hidden.</b> The ring reaches the journal one Hangfire hop after
/// the provider sent it, which is adequate for a journal and is NOT what a screen pop needs — the
/// agent's telephone is already ringing. TEL-B-10's push is Lot 3 and will read this row; the
/// alternative, writing a Ringing row straight from the parsed body, was not taken because the
/// body carries no numbers (<c>ProviderWebhookEvent</c> is deliberately an identifier, a type and
/// a status) and a call row with no numbers is a row the caller-id lookup can never attach.
/// </para>
/// </summary>
[Queue(IntegrationSyncJobQueue)]
public sealed class ProcessTelephonyWebhookJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// <c>Features.Sync.IntegrationSyncJob.QueueName</c>, repeated as a literal because a
    /// <c>[Queue]</c> attribute takes a compile-time constant and that one is a <c>const</c> on a
    /// type this file would otherwise have no reason to reach. <b>The host must run a worker on
    /// this queue</b> — see <c>SyncServiceRegistration</c>; a job enqueued to a queue nobody reads
    /// never runs at all, and nothing says so.
    /// </summary>
    private const string IntegrationSyncJobQueue = "integration-sync";

    public async Task ExecuteAsync(
        Guid tenantId, Guid connectionId, Guid eventId, DateTimeOffset receivedAt)
    {
        // Set BEFORE the scope is created: ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them, and a scope opened first captures the HTTP
        // implementations and finds no request. SYSTEM — a provider sent this, not a person.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<ProcessTelephonyWebhookJob>>();

        await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<TelephonyAccess>(),
            sp.GetRequiredService<TelCallJournalWriter>(),
            sp.GetRequiredService<TelephonyPayloadProtector>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            connectionId,
            eventId,
            receivedAt,
            CancellationToken.None);
    }

    /// <summary>
    /// The processing, with its collaborators passed in so a test can pin it without a DI
    /// container or a Hangfire storage.
    ///
    /// <para>
    /// <b>The event row and the call row commit together</b>, in one <c>SaveChanges</c>. An event
    /// marked processed whose call was not written would be a gap nothing ever looks at again; a
    /// call written while the event stays unprocessed would be re-processed by a retry, which is
    /// harmless but makes "what is still pending" unreadable.
    /// </para>
    /// </summary>
    internal static async Task RunAsync(
        IntegrationDbContext db,
        TelephonyAccess access,
        TelCallJournalWriter journal,
        TelephonyPayloadProtector payloads,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid connectionId,
        Guid eventId,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        // Both halves of the composite key. The table is partitioned by received_at, so a lookup
        // on the id alone would scan every partition — and the instant is already in hand.
        var stored = await db.TelWebhookEvents
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.Id == eventId && e.ReceivedAt == receivedAt, ct);

        if (stored is null)
        {
            // Nothing to do and nothing wrong: a retry after the row's partition was detached, or
            // a job queued by a deployment that was then rolled back.
            logger.LogWarning(
                "Telephony webhook event {EventId} of tenant {TenantId} is no longer stored; "
                + "skipped.", eventId, tenantId);

            return;
        }

        if (stored.ProcessedAt is not null)
        {
            // Hangfire retries at least once, and the dedup index guards the RECEIPT rather than
            // the processing. This is the second guard, and it is the cheap one.
            logger.LogDebug(
                "Telephony webhook event {EventId} was already processed; skipped.", eventId);

            return;
        }

        var use = await access.ResolvePortAsync(tenantId, IntegrationCapability.CallEvents, ct);

        if (use.IsFailure)
        {
            // Telephony was switched off between the receipt and now, which is a deliberate act.
            // Recorded on the row so an operator can see events arriving for a connection nobody
            // is listening to any more, rather than silently dropped.
            await FinishAsync(db, stored, $"SKIPPED:{use.Code}", clock, ct);

            return;
        }

        var parsed = CallIdOf(use.Value.Port, payloads, stored, logger);

        if (parsed is null)
        {
            // A body that will not open, or a parse that yields no call id. Finished rather than
            // retried: neither can change on a second attempt, and the row keeps saying so.
            await FinishAsync(db, stored, "SKIPPED:NO_CALL_ID", clock, ct);

            return;
        }

        var call = await use.Value.Port.GetCallAsync(new ExternalId(parsed), ct);

        if (call.IsFailure)
        {
            // THROWN, not swallowed, and only for a retryable failure: the whole value of this
            // path is the authoritative re-read, and giving up on a timeout would leave a call
            // out of the journal for good. A non-retryable failure — the provider does not know
            // this call — is recorded and finished, because retrying produces the same answer.
            if (call.IsRetryable)
            {
                throw new InvalidOperationException(
                    $"Telephony call {parsed} could not be re-read ({call.Code}); Hangfire will "
                    + "retry.");
            }

            logger.LogWarning(
                "Telephony webhook event {EventId}: the provider does not resolve call "
                + "{ExternalCallId} ({Code}).", eventId, parsed, call.Code);

            await FinishAsync(db, stored, $"FAILED:{call.Code}", clock, ct);

            return;
        }

        var row = await journal.RecordAsync(tenantId, connectionId, call.Value, ct);

        stored.MarkProcessed($"CALL:{row.Status}", clock.GetUtcNow());

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Telephony webhook event {EventId} of tenant {TenantId} journalled call "
            + "{ExternalCallId} as {Status} ({MatchStatus}).",
            eventId, tenantId, row.ExternalCallId, row.Status, row.MatchStatus);
    }

    /// <summary>
    /// The provider's call id, re-parsed from the stored body.
    ///
    /// <para>
    /// Re-parsed rather than carried in the job's arguments, and the reason is the rule about
    /// Hangfire arguments: they are rows in the shared application database and lines on a
    /// dashboard page. A provider's call identifier is not a secret, but it is the key that joins
    /// a dashboard entry to one customer's conversation, and the job can find it again from a row
    /// it is already loading.
    /// </para>
    ///
    /// <para>
    /// This is the ONLY reader of <c>payload_enc</c> in the module, and it reads it for an
    /// identifier and nothing else: the statuses, the numbers and the durations all come from the
    /// authoritative re-read, never from the body.
    /// </para>
    /// </summary>
    private static string? CallIdOf(
        ITelephonyPort port,
        TelephonyPayloadProtector payloads,
        TelWebhookEvent stored,
        ILogger logger)
    {
        var body = payloads.Reveal(stored.PayloadEnc);

        if (body is null)
        {
            logger.LogWarning(
                "Telephony webhook event {EventId}: the stored payload could not be opened.",
                stored.Id);

            return null;
        }

        try
        {
            var parsed = port.ParseWebhook(new TelephonyWebhookRequest(
                System.Text.Encoding.UTF8.GetString(body),
                new Dictionary<string, string>()));

            return parsed.CallId?.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The parser's contract forbids throwing and the contract suite pins it. The guard
            // stays because the input reached us from outside: the exception TYPE is logged and
            // the body is not, since it carries both numbers of a call.
            logger.LogWarning(
                "Telephony webhook event {EventId}: the parser threw {Reason}, which its contract "
                + "forbids.", stored.Id, ex.GetType().Name);

            return null;
        }
    }

    private static async Task FinishAsync(
        IntegrationDbContext db,
        TelWebhookEvent stored,
        string outcome,
        TimeProvider clock,
        CancellationToken ct)
    {
        stored.MarkProcessed(outcome, clock.GetUtcNow());

        await db.SaveChangesAsync(ct);
    }
}
