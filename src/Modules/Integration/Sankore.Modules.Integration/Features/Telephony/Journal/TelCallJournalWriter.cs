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
    TelephonyAgencyResolver agencies,
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
    /// <b>It reports whether the STATUS changed on this reading</b>, which the caller needs and
    /// cannot work out for itself: it never saw the previous status. The realtime pushes of
    /// TEL-B-10 fire on a change and not on a reading — a provider that redelivers the same
    /// event, or a catch-up sweep passing over a call it already knows, would otherwise re-open
    /// an agent's call panel for a conversation that ended an hour ago.
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
    internal async Task<TelCallReading> RecordAsync(
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

        // A row that did not exist a moment ago has, by definition, just reached the status the
        // provider reports — there was no previous status for it to differ from. Said explicitly
        // rather than left implicit, because the realtime push downstream fires on a CHANGE and a
        // newly opened ringing call is the most important change there is.
        var statusChanged = existing is null;

        if (existing is not null)
        {
            if (row.TryMoveTo(call.Status, call.EndedAt ?? call.AnsweredAt ?? call.StartedAt))
            {
                statusChanged = true;
            }
            else
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

        // TEL-B-19's repair pass. Only for a row that has an agent and no agency — which is
        // either a call opened while M12 was unreachable, or one whose agent was attributed later
        // by a click-to-call correlation. StampAgency refuses to overwrite, so a row that already
        // has one costs nothing but the null check.
        if (row.CrmUserId is { } attributed && row.AgencyId is null)
            row.StampAgency(await agencies.ResolveAsync(tenantId, attributed, ct));

        // The recording FLAG, not a recording. Whether SANKORE keeps a copy is TEL-B-15's policy
        // and TEL-B-16's store; all this says is that the provider has one to fetch.
        if (call.HasRecording && row.RecordingStatus == TelRecordingStatus.NotRecorded)
            row.SetRecordingStatus(TelRecordingStatus.Pending);

        await TryAttachAsync(tenantId, row, call, ct);

        return new TelCallReading(row, statusChanged);
    }

    private async Task<TelCall> OpenAsync(
        Guid tenantId, Guid connectionId, ProviderCall call, CancellationToken ct)
    {
        var from = numbers.Protect(call.FromNumber);
        var to = numbers.Protect(call.ToNumber);

        // The CRM user comes from tel_user_link. A call from an account nobody linked is
        // journalled with a null user and listed under "calls to attribute" — an ordinary state,
        // not an error, and the reason the nightly link check exists.
        var crmUserId = call.ProviderUserId is { } providerUserId
            ? await ResolveCrmUserAsync(tenantId, connectionId, providerUserId.Value, ct)
            : null;

        // TEL-B-06's correlation, and it happens HERE rather than on a later reading because
        // ClickToCallId is set at creation: a call opened without it would have to be re-matched
        // by a sweep, and by then the two-minute window has closed.
        var request = await CorrelateAsync(tenantId, connectionId, call, crmUserId, ct);

        // A correlated request is ALSO an answer to "whose call was this", and a better one than
        // the link lookup: the agent pressed the button. It fills the gap for a provider that
        // reports no user id on the call itself — without it such a call would be journalled
        // unattributed even though this module started it.
        crmUserId ??= request?.CrmUserId;

        // After the two attributions and not before either of them: the agency is a property OF
        // the agent, so resolving it earlier would miss the call that only the click-to-call
        // correlation knows the owner of.
        var agencyId = await agencies.ResolveAsync(tenantId, crmUserId, ct);

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
            crmUserId,
            request?.Id,
            agencyId);

        db.TelCalls.Add(row);

        if (request is not null)
        {
            // The agent said which record they were calling, which is the ONLY certain attachment
            // this module ever gets — every other one is a number resolving to somebody. It is
            // written as Manual-grade evidence under its own method so a later sweep cannot
            // quietly replace it with a number match.
            var attached = row.Attach(
                TelMatchStatus.Matched, TelMatchMethod.ClickToCall,
                request.EntityType, request.EntityId);

            if (attached.IsSuccess)
                request.MarkConnected();
        }

        return row;
    }

    /// <summary>
    /// The click-to-call request this provider call answers, or null (TEL-B-06, §4.2).
    ///
    /// <para>
    /// <b>By the provider's own reference first, and by (user, number, window) only as a
    /// fallback.</b> A reference is an identity the provider gave us and is exact; the fallback is
    /// a guess that is right almost always and would be wrong in the one case that matters — an
    /// agent who clicked twice on two different records within two minutes. Which is why the
    /// window is two minutes and not ten, and why <c>ITelephonyPort.StartCallbackAsync</c>'s
    /// contract asks every adapter for a reference even though the contract suite allows one that
    /// cannot produce a useful one.
    /// </para>
    ///
    /// <para>
    /// Only OUTBOUND calls correlate. An inbound call is somebody ringing us, and a click-to-call
    /// in flight at the same moment is a coincidence — attaching it would file an incoming
    /// conversation against whatever record the agent happened to be looking at.
    /// </para>
    ///
    /// <para>
    /// The number is compared on its BLIND INDEX, never in clear and never by decrypting the
    /// stored one: the index is the only comparison that is correct here, and it is also the only
    /// one that does not put a number in this method's memory.
    /// </para>
    /// </summary>
    private async Task<TelClickToCall?> CorrelateAsync(
        Guid tenantId, Guid connectionId, ProviderCall call, Guid? crmUserId, CancellationToken ct)
    {
        if (call.Direction != TelCallDirection.Outbound) return null;

        var externalRef = call.Id.Value;

        // AsTracking on both reads: a correlated request is marked Connected, which is a write.
        var byReference = await db.TelClickToCalls
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId
                  && r.ConnectionId == connectionId
                  && r.ExternalRef == externalRef,
                ct);

        if (byReference is not null) return byReference;

        var remoteIndex = numbers.IndexOf(call.ToNumber);

        var query = db.TelClickToCalls
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(r => r.TenantId == tenantId
                     && r.ConnectionId == connectionId
                     && r.TargetNumberBi == remoteIndex);

        // Narrowed by user when the provider told us which account placed the call, and NOT
        // narrowed when it did not. Both halves matter. A provider that reports the user gives us
        // a cheap exact filter; one that reports nothing — and a callback API that returns neither
        // a reference nor a user id is a real shape — would otherwise make this fallback
        // unreachable, so the agent's own call would be journalled as an outbound to a number
        // nobody recognises while the request that started it sat two rows away.
        if (crmUserId is { } userId)
            query = query.Where(r => r.CrmUserId == userId);

        var candidates = await query.OrderByDescending(r => r.RequestedAt).ToListAsync(ct);

        // IsCorrelatableAt is the aggregate's own rule — status plus the window — evaluated in
        // memory because it is not a predicate EF can translate.
        var correlatable = candidates
            .Where(r => r.IsCorrelatableAt(call.StartedAt))
            .ToList();

        if (correlatable.Count == 0) return null;

        // Ambiguity is measured in AGENTS, not in requests, and the distinction is the whole rule.
        //
        // Two agents of one branch ringing the same shop within two minutes is ordinary, and
        // crediting one with the other's conversation is not a small error — so two distinct
        // agents means no click-to-call attachment at all, and the number match takes over. That
        // attaches the right RECORD and leaves the agent unattributed, which is the honest of the
        // two wrong answers.
        //
        // Two requests from ONE agent, on the other hand, is a double-click: they meant the
        // second, which the ordering above has already put first.
        return correlatable.DistinctBy(r => r.CrmUserId).Count() == 1
            ? correlatable[0]
            : null;
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


/// <summary>
/// One reading of one call: the row, and whether this reading MOVED it.
///
/// <para>
/// A record and not a tuple, so the boolean has a name at every call site — <c>if (changed)</c>
/// reads as a condition about the call, where <c>if (result.Item2)</c> reads as nothing at all.
/// </para>
/// </summary>
/// <param name="StatusChanged">
/// True when the row was created, or when <c>TryMoveTo</c> accepted a new status. False when the
/// provider reported the status the row already had, or one the transition table refuses — both
/// of which are ordinary and neither of which is news for a browser.
/// </param>
internal sealed record TelCallReading(TelCall Call, bool StatusChanged);
