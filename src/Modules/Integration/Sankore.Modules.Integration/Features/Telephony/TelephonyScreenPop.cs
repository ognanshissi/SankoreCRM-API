namespace Sankore.Modules.Integration.Features.Telephony;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Kernel.Realtime;

/// <summary>
/// Tells one agent their telephone is ringing (TEL-B-10).
///
/// <para>
/// <b>The message carries a call id and NOTHING else</b> — no number, no name, not even the
/// direction. Three reasons, and the first alone decides it: a websocket frame is delivered to
/// whoever holds the connection, with no permission check and no agency perimeter per frame, so a
/// name travelling here would be personal data leaving the building on the strength of a
/// connection nobody re-authorises. The screen then calls <c>GET telephony/calls/{id}/context</c>,
/// which applies both. Second, the push is best-effort and the context read is not — a screen
/// that rendered from the push would show one thing to an agent whose tab was open and nothing to
/// one who had just reloaded. Third, a payload is a contract: every field added here is a field
/// some front end starts depending on, and a call id is the only one that cannot go stale.
/// </para>
///
/// <para>
/// <b>Pushed only to a resolved CRM user.</b> A call whose provider account nobody linked has no
/// agent to tell — it is journalled and appears in the "calls to attribute" list, which is the
/// honest outcome.
/// </para>
///
/// <para>
/// <b>THREE events, where TEL-B-10 shipped with one — and the one was not enough.</b> The
/// original reasoning here was that later transitions need no push because "the agent is holding
/// the handset by then and knows perfectly well what happened". That is true of the AGENT and
/// false of the SCREEN, which is the thing being told: the panel has to start a timer when the
/// call is answered and swap itself for the qualification form when it ends, and with a ring as
/// its only signal it has nothing to do either on. The alternatives were worse — a browser
/// polling the context endpoint every second for every ringing call, or a panel dismissed by a
/// timer that is wrong whenever a conversation runs long.
/// </para>
///
/// <para>
/// <b>Each of the three carries the same single field, and that part does not change.</b> The
/// argument above is about WHEN to push, never about what to put in a frame: the payload stays a
/// call id, and a screen that wants to know more asks the context endpoint, which applies the
/// permission and the agency perimeter a websocket frame cannot.
/// </para>
///
/// <para>
/// <b>Pushed on a CHANGE of status, not on a reading.</b> <c>TelCallReading.StatusChanged</c> is
/// what makes that possible. A provider redelivering an event, or the catch-up sweep passing over
/// a call it already knows, would otherwise re-open an agent's panel for a conversation that
/// ended an hour ago — and the sweep walks up to twenty pages per run.
/// </para>
/// </summary>
internal sealed class TelephonyScreenPop(
    IRealtimeNotifier notifier,
    ILogger<TelephonyScreenPop> logger)
{
    /// <summary>
    /// The event name the browser subscribes to. Dotted, stable, and a contract: renaming it
    /// silently stops every screen pop, with nothing failing on either side.
    /// </summary>
    internal const string RingingEvent = "telephony.call.ringing";

    /// <summary>The conversation started. The panel starts its timer.</summary>
    internal const string AnsweredEvent = "telephony.call.answered";

    /// <summary>
    /// The call is over, however it ended — picked up and finished, missed, diverted to the
    /// mailbox, or failed. ONE event for the four terminal statuses, deliberately: the panel's
    /// reaction is the same (stop, offer the qualification), and the status itself is on the row
    /// the screen is about to read anyway.
    /// </summary>
    internal const string EndedEvent = "telephony.call.ended";

    /// <summary>
    /// Pushes the transition this reading produced, if it is one a screen cares about.
    ///
    /// <para>
    /// Takes the whole <see cref="TelCall"/> plus the reading's own
    /// <paramref name="statusChanged"/>, so every condition — did it move, which event does this
    /// status mean, does anybody own it — is decided here rather than at each call site. There
    /// are two call sites and there will be three, and a condition duplicated three times is a
    /// condition that differs in one.
    /// </para>
    ///
    /// <para>
    /// Never throws and never fails its caller: the caller is journalling a conversation that
    /// actually happened, and a push is a convenience for a telephone that is ringing right now.
    /// </para>
    /// </summary>
    internal async Task PushTransitionAsync(TelCall call, bool statusChanged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        // Nothing moved: the provider repeated itself, or the transition table refused. Neither
        // is news for a browser.
        if (!statusChanged) return;

        if (call.CrmUserId is not { } userId) return;

        var eventName = EventFor(call.Status);

        // InProgress has no event of its own beyond Answered, and Ringing→Ringing cannot happen
        // here. A status this method does not name is one no screen reacts to.
        if (eventName is null) return;

        var pushed = await notifier.PushToUserAsync(
            call.TenantId, userId, eventName, new TelephonyCallPush(call.Id), ct);

        if (!pushed)
        {
            // Debug: an agent with no open tab is the ordinary case for most of the day, and a
            // warning per unanswered ring would be the bulk of a night's logs.
            logger.LogDebug(
                "Nobody was listening for {EventName} of call {CallId}; it is journalled either "
                + "way.", eventName, call.Id);
        }
    }

    /// <summary>
    /// Which event a status means, or <c>null</c> for a status no screen reacts to.
    ///
    /// <para>
    /// A <c>switch</c> over the enum with no <c>default</c> arm returning something plausible: a
    /// seventh status added to <see cref="TelCallStatus"/> lands in the discard arm and pushes
    /// nothing, which is the safe direction — a wrong event would have a panel declare a live
    /// conversation over.
    /// </para>
    /// </summary>
    private static string? EventFor(TelCallStatus status)
        => status switch
        {
            TelCallStatus.Ringing => RingingEvent,
            TelCallStatus.InProgress => AnsweredEvent,

            // The four terminal statuses share one event: the panel's reaction is identical, and
            // which of the four it was is on the row the screen reads next.
            TelCallStatus.Completed or TelCallStatus.Missed
                or TelCallStatus.Voicemail or TelCallStatus.Failed => EndedEvent,

            _ => null,
        };
}

/// <summary>
/// The whole payload, for all three events. One field, and adding a second needs the argument in
/// <see cref="TelephonyScreenPop"/> answered first.
///
/// <para>
/// One type for the three rather than three identical records, because the three frames ARE the
/// same shape and a reader comparing them should see that immediately — and because three types
/// would invite one of them to grow a field.
/// </para>
/// </summary>
internal sealed record TelephonyCallPush(Guid CallId);
