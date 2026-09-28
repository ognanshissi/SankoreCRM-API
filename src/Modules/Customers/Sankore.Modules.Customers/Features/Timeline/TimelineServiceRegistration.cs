namespace Sankore.Modules.Customers.Features.Timeline;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Features.Timeline.Loyalty;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Features.Timeline.Segments;

/// <summary>
/// DI wiring of the Timeline zone. Called by <c>CustomersModule.AddCustomersModule</c>.
///
/// Not registered here (by design, they are the host's business):
///   * the recurring-job CRONS — declared in <c>Program.cs</c>:
///       <c>customers-segment-assignment-orchestrator</c>      → <c>0 3 * * *</c>
///       <c>customers-loyalty-score-orchestrator</c>           → <c>30 3 * * *</c>
///     Both are GLOBAL (one registration, not one per tenant): they fan out per-tenant jobs
///     themselves, so adding a tenant needs no new recurring registration.
///   * the MassTransit consumers — each needs an explicit <c>x.AddConsumer&lt;…&gt;()</c> line
///     in <c>Program.cs</c>: ClientCreatedTimelineConsumer, ClientActivatedTimelineConsumer,
///     ClientSuspendedTimelineConsumer, ClientArchivedTimelineConsumer,
///     ClientTransferredTimelineConsumer, ClientsMergedTimelineConsumer,
///     ClientSegmentChangedTimelineConsumer, GroupMembershipChangedTimelineConsumer.
/// </summary>
internal static class TimelineServiceRegistration
{
    internal static IServiceCollection AddTimelineServices(this IServiceCollection s)
    {
        // Scoped: it writes through the request/message-scoped CustomersDbContext.
        s.AddScoped<IClientTimelineProjector, ClientTimelineProjector>();

        // Hangfire activates job types from the container; transient because each invocation
        // is independent and the jobs hold no state between runs.
        s.AddTransient<AssignSegmentsOrchestratorJob>();
        s.AddTransient<AssignSegmentsJob>();
        s.AddTransient<ComputeLoyaltyScoresOrchestratorJob>();
        s.AddTransient<ComputeLoyaltyScoresJob>();

        return s;
    }
}
