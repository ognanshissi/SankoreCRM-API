namespace Sankore.Modules.Integration.Features.Telephony.Journal;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;

/// <summary>
/// Writes one call into the journal, and writes it the same way whichever path brought it
/// (TEL-B-08).
///
/// <para>
/// <b>One writer for three callers</b> — the webhook, the catch-up sweep of TEL-B-14 and the
/// click-to-call correlation of TEL-B-06 — because they converge on one row by construction:
/// <c>ux_tel_call_external</c> is unique on (tenant, connection, provider call id). Three copies
/// of "insert or update" would be three opinions on which of them may overwrite the other's
/// status, and the one that loses is always the one a human looked at.
/// </para>
///
/// <para>
/// <b>It does not save.</b> Every caller is inside a transaction that owns more than this row —
/// the webhook event marked processed, the sweep's cursor — and a save here would commit the call
/// before the thing that proves it was handled.
/// </para>
/// </summary>
internal sealed class TelCallJournalWriter(
    IntegrationDbContext db,
    TelephonyNumberProtector numbers,
    TelephonyCallMatcher matcher,
    ILogger<TelCallJournalWriter> logger)
{
    /// <summary>
    /// The row for this provider call, created or brought up to date.
    ///
    /// <para>
    /// <b>The provider's reading is authoritative for the status, never the other way round.</b>
    /// A transition the table forbids is LOGGED and the call keeps its last known status —
    /// <c>TelCall.TryMoveTo</c> answers false rather than throwing, because out-of-order delivery
    /// is ordinary and handing an error back would have the provider redeliver, for hours, an
    /// event that will never be accepted.
    /// </para>
    ///
    /// <para>
    /// <b>Attachment is attempted on every reading until it succeeds</b>, not only at creation: a
    /// ring arrives before anything is known, and the lead that number belongs to may be captured
    /// thirty seconds later by the agent who just answered. A row already attached is left alone,
    /// and a MANUAL attachment is never touched — <c>TelCall.Attach</c> refuses that, and the
    /// refusal is read here as "nothing to do" rather than as a failure.
    /// </para>
    /// </summary>
    internal async Task<TelCall> RecordAsync(
        Guid tenantId, Guid connectionId, ProviderCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var externalId = call.Id.Value;

        // IgnoreQueryFilters with an explicit tenant predicate: every caller is a webhook handler
        // or a Hangfire job, where the tenant is an argument and the ambient context is SYSTEM.
        var existing = await db.TelCalls
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId
                  && c.ConnectionId == connectionId
                  && c.ExternalCallId == externalId,
                ct);

        var row = existing ?? await OpenAsync(tenantId, connectionId, call, ct);

        if (existing is not null)
        {
            if (!row.TryMoveTo(call.Status, call.EndedAt ?? call.AnsweredAt ?? call.StartedAt))
            {
                // Not an error, and not silent either: an operator chasing a call stuck in
                // Ringing needs to see that the provider reported something the table refused.
                logger.LogInformation(
                    "Telephony journal: call {ExternalCallId} of tenant {TenantId} stays "
                    + "{Current} — the provider reported {Reported}, which is not a transition "
                    + "the journal allows.",
                    externalId, tenantId, row.Status, call.Status);
            }
        }

        // Both on EVERY reading, including the first: a provider that reports a completed call
        // after its "answered" webhook was lost carries the answer instant on the re-read, and
        // recording instants only at creation would leave that row with a talk duration and no
        // moment the conversation began.
        row.RecordInstants(call.AnsweredAt, call.EndedAt);
        row.RecordDurations(call.RingSeconds, call.TalkSeconds);

        // The recording FLAG, not a recording. Whether SANKORE keeps a copy is TEL-B-15's policy
        // and TEL-B-16's store; all this says is that the provider has one to fetch.
        if (call.HasRecording && row.RecordingStatus == TelRecordingStatus.NotRecorded)
            row.SetRecordingStatus(TelRecordingStatus.Pending);

        await TryAttachAsync(tenantId, row, call, ct);

        return row;
    }

    private async Task<TelCall> OpenAsync(
        Guid tenantId, Guid connectionId, ProviderCall call, CancellationToken ct)
    {
        var from = numbers.Protect(call.FromNumber);
        var to = numbers.Protect(call.ToNumber);

        // The CRM user comes from tel_user_link and from nothing else. A call from an account
        // nobody linked is journalled with a null user and listed under "calls to attribute" —
        // an ordinary state, not an error, and the reason the nightly link check exists.
        var crmUserId = call.ProviderUserId is { } providerUserId
            ? await ResolveCrmUserAsync(tenantId, connectionId, providerUserId.Value, ct)
            : null;

        var row = TelCall.Open(
            tenantId,
            connectionId,
            call.Id.Value,
            call.Direction,
            call.Status,
            from.Encrypted,
            from.BlindIndex,
            to.Encrypted,
            to.BlindIndex,
            call.StartedAt,
            call.ProviderUserId?.Value,
            crmUserId);

        db.TelCalls.Add(row);

        return row;
    }

    private async Task<Guid?> ResolveCrmUserAsync(
        Guid tenantId, Guid connectionId, string externalUserId, CancellationToken ct)
    {
        // A BROKEN link still resolves, on purpose. The provider account stopped being listed; the
        // agent who owned it is still the person who had the conversation, and refusing to
        // attribute their calls until somebody repairs a settings row would lose exactly the
        // history the repair is supposed to explain. Only a DISABLED link stops resolving —
        // somebody said, with a reason, that this pairing no longer holds.
        return await db.TelUserLinks
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId
                     && l.ConnectionId == connectionId
                     && l.ExternalUserId == externalUserId
                     && l.Status != TelUserLinkStatus.Disabled)
            .Select(l => (Guid?)l.CrmUserId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Resolves the REMOTE number — the other end of the call, which is the only one that can be
    /// a customer. For an inbound call that is the caller; for an outbound one, the callee.
    /// Getting this backwards would resolve the institution's own switchboard on every call.
    /// </summary>
    private async Task TryAttachAsync(
        Guid tenantId, TelCall row, ProviderCall call, CancellationToken ct)
    {
        // Already attached, or already left ambiguous for a human: nothing to redo. An Internal
        // call is settled too — both ends are ours.
        if (row.MatchStatus != TelMatchStatus.Unknown || row.EntityId is not null)
            return;

        var remoteNumber = call.Direction == TelCallDirection.Inbound
            ? call.FromNumber
            : call.ToNumber;

        var match = await matcher.ResolveAsync(tenantId, remoteNumber, ct);

        if (match.Status == TelMatchStatus.Unknown)
            return;

        var attached = row.Attach(
            match.Status, TelMatchMethod.Number, match.EntityType, match.EntityId);

        if (attached.IsFailure)
        {
            // The one reachable refusal is ManualAttachmentWins, and it means a human has already
            // read this call. Logged at debug: it is the system working, not a problem.
            logger.LogDebug(
                "Telephony journal: call {ExternalCallId} of tenant {TenantId} keeps its "
                + "attachment ({Error}).", row.ExternalCallId, tenantId, attached.Error);
        }
    }
}
