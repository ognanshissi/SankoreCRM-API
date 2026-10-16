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
/// <b>Pushed only on the RING, and only to a resolved CRM user.</b> A call whose provider account
/// nobody linked has no agent to tell — it is journalled and appears in the "calls to attribute"
/// list, which is the honest outcome. Later transitions are not pushed: the agent is holding the
/// handset by then and knows perfectly well what happened.
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

    /// <summary>
    /// Pushes the ring when this reading of the call is one worth pushing.
    ///
    /// <para>
    /// Takes the whole <see cref="TelCall"/> rather than a call id and a user id, so the two
    /// conditions — it is ringing, somebody owns it — are decided here instead of at each call
    /// site. There will be three of them: the webhook, the catch-up sweep and the click-to-call
    /// correlation, and a condition duplicated three times is a condition that differs in one.
    /// </para>
    ///
    /// <para>
    /// Never throws and never fails its caller: the caller is journalling a conversation that
    /// actually happened, and a push is a convenience for a telephone that is ringing right now.
    /// </para>
    /// </summary>
    internal async Task PushRingingAsync(TelCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (call.Status != TelCallStatus.Ringing) return;

        if (call.CrmUserId is not { } userId) return;

        var pushed = await notifier.PushToUserAsync(
            call.TenantId, userId, RingingEvent, new TelephonyRingingPush(call.Id), ct);

        if (!pushed)
        {
            // Debug: an agent with no open tab is the ordinary case for most of the day, and a
            // warning per unanswered ring would be the bulk of a night's logs.
            logger.LogDebug(
                "Nobody was listening for the ring of call {CallId}; it is journalled either way.",
                call.Id);
        }
    }
}

/// <summary>
/// The whole payload. One field, and adding a second needs the argument in
/// <see cref="TelephonyScreenPop"/> answered first.
/// </summary>
internal sealed record TelephonyRingingPush(Guid CallId);
