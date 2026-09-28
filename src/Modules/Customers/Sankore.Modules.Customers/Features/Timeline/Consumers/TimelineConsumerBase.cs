namespace Sankore.Modules.Customers.Features.Timeline.Consumers;

using MassTransit;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Common skeleton of every timeline consumer, so the two non-negotiable rules cannot be
/// forgotten in a new one:
///
///   1. <see cref="IInboxGuard"/> FIRST. A redelivered message id exits silently — not an
///      error, not a retry: at-least-once delivery is the normal case, not a failure.
///   2. SYSTEM identity. A consumer has no HTTP context, so
///      <see cref="BackgroundJobContext.SetScope(Guid, Guid, string)"/> installs the tenant
///      and an actor of <see cref="Guid.Empty"/> / <c>"SYSTEM"</c>: the projection is
///      attributed to the system account, never to whoever happened to trigger the
///      originating command.
///
/// Subclasses only describe WHAT to project. Adding a producer module (M02/M03/M04/M08)
/// means adding a subclass here plus one <c>x.AddConsumer&lt;…&gt;()</c> line in
/// <c>Program.cs</c> — nothing else in the zone changes.
/// </summary>
public abstract class TimelineConsumerBase<TEvent>(IInboxGuard inbox) : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    public async Task Consume(ConsumeContext<TEvent> context)
    {
        var evt = context.Message;
        var tenantId = TenantIdOf(evt);
        var ct = context.CancellationToken;

        // The transport message id changes on a republish; the inbox keys on it because
        // its job is "have I already handled THIS delivery".
        var messageId = context.MessageId ?? evt.EventId;

        if (!await inbox.TryBeginAsync(messageId, tenantId, typeof(TEvent).Name, ct))
            return;

        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");

        await ProjectAsync(evt, ct);
    }

    /// <summary>Tenant carried by the event — always its first field, per the M01 event contract.</summary>
    protected abstract Guid TenantIdOf(TEvent evt);

    /// <summary>Projects the event. Runs under the SYSTEM scope, after the inbox accepted it.</summary>
    protected abstract Task ProjectAsync(TEvent evt, CancellationToken ct);
}
