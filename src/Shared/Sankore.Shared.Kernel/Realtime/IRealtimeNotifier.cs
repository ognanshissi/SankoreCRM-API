namespace Sankore.Shared.Kernel.Realtime;

/// <summary>
/// Pushes a short message to one signed-in user's open browser tabs (TEL-B-10).
///
/// <para>
/// In the Kernel, with the transport in <c>Sankore.Shared.Realtime</c>, for the reason
/// <c>ISecretsModule</c> is here and <c>AesSecretsModule</c> is not: a module that needs to tell
/// an agent something must not have to reference a hub, and a host that pushes nothing must not
/// have to ship one.
/// </para>
///
/// <para>
/// <b>A push is a NUDGE and never a payload.</b> What travels is an event name and the identifier
/// of something the receiver can then go and read under its own permissions — for telephony, a
/// call id and nothing else. Two reasons, and the second is the one that matters: a websocket
/// frame is delivered to whoever holds the connection, so a push carrying a customer's name and
/// number would be personal data leaving the building on the strength of a connection nobody
/// re-authorises per frame; and the read that follows applies the agency perimeter and the
/// permission, which a push cannot.
/// </para>
///
/// <para>
/// <b>Delivery is best-effort by construction.</b> There is no queue behind this and no retry: a
/// browser that is not connected right now has no inbox, and a screen pop for a telephone that
/// has stopped ringing is worse than none. Every caller treats a false answer as ordinary — the
/// call is journalled either way, and the agent finds it in their list.
/// </para>
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>
    /// Pushes <paramref name="eventName"/> with <paramref name="payload"/> to every connection
    /// this user holds, for this tenant.
    ///
    /// <para>
    /// The tenant is a parameter and not read from an ambient context on purpose: the callers are
    /// a webhook handler and a Hangfire job, where the ambient identity is SYSTEM. It is also part
    /// of the group name, so a user id that existed in two tenants could not receive the other's
    /// pushes.
    /// </para>
    /// </summary>
    /// <param name="eventName">
    /// Dotted and stable, e.g. <c>telephony.call.ringing</c>. It is a contract with the browser:
    /// renaming one silently stops a screen reacting, so treat it as an API.
    /// </param>
    /// <param name="payload">
    /// Identifiers only. See the type's remarks — this is not the place for a name or a number.
    /// </param>
    /// <returns>
    /// Whether the push was handed to the transport. <c>false</c> means nobody was listening or
    /// no transport is configured, and is never an error.
    /// </returns>
    Task<bool> PushToUserAsync(
        Guid tenantId, Guid userId, string eventName, object payload, CancellationToken ct);
}

/// <summary>
/// The notifier a host that pushes nothing gets. Registered by default so no module has to
/// null-check and no deployment has to run a hub to journal a call.
///
/// <para>
/// It answers <c>false</c> rather than throwing, which is the same answer a configured transport
/// gives for a user with no open tab — so the callers have one path, not two.
/// </para>
/// </summary>
public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task<bool> PushToUserAsync(
        Guid tenantId, Guid userId, string eventName, object payload, CancellationToken ct)
        => Task.FromResult(false);
}
