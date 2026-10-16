namespace Sankore.Shared.Realtime;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel.Realtime;

/// <summary>
/// The one implementation of <see cref="IRealtimeNotifier"/> over
/// <see cref="SankoreRealtimeHub"/>.
///
/// <para>
/// <b>It never throws, and that is its whole job.</b> Its callers are a webhook handler and a
/// Hangfire job in the middle of journalling a call: a hub whose Redis backplane is unreachable
/// must not take the journal down with it, because the journal is the record of a conversation
/// that actually happened and the push is a convenience for a telephone that is ringing right
/// now. Every failure is logged and answered <c>false</c> — the same answer as "nobody is
/// listening", so callers have one path.
/// </para>
///
/// <para>
/// <b>It also cannot tell whether anybody received it.</b> SignalR's group send is fire and
/// forget; <c>true</c> here means the message was handed to the transport, not that a tab showed
/// anything. Promising more would invite a caller to treat a push as delivery.
/// </para>
/// </summary>
internal sealed class SignalRRealtimeNotifier(
    IHubContext<SankoreRealtimeHub> hub,
    ILogger<SignalRRealtimeNotifier> logger) : IRealtimeNotifier
{
    public async Task<bool> PushToUserAsync(
        Guid tenantId, Guid userId, string eventName, object payload, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
        {
            // Guid.Empty is the SYSTEM placeholder, not a person. A push addressed to it would
            // land in a group every background context would share.
            logger.LogWarning(
                "A realtime push to {EventName} named no tenant or no user; dropped.", eventName);

            return false;
        }

        if (string.IsNullOrWhiteSpace(eventName))
            return false;

        try
        {
            await hub.Clients
                .Group(SankoreRealtimeHub.UserGroup(tenantId, userId))
                .SendAsync(eventName, payload, ct);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The payload is NOT logged. It is identifiers only by contract, but a log line is
            // where a future payload that should not have existed would survive for months.
            logger.LogWarning(
                ex, "Realtime push {EventName} to user {UserId} of tenant {TenantId} failed; the "
                  + "work that produced it is unaffected.", eventName, userId, tenantId);

            return false;
        }
    }
}
