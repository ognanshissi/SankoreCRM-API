using Sankore.Modules.Kyc.PublicApi;

namespace Sankore.Modules.Customers.Features.Lifecycle.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Refreshes the AML risk rating carried by the client record (US-M01-BE-13).
///
/// A risk change is NOT a lifecycle change: <c>Status</c> is left untouched even
/// when the level becomes <c>High</c>. Escalating to a suspension is a compliance
/// decision taken by a human through the suspend endpoint, never a side effect of
/// a scoring event — which is also why nothing is written to the status history
/// here.
///
/// Runs outside any HTTP request: <c>IgnoreQueryFilters()</c> plus an explicit
/// TenantId predicate from the event payload.
/// </summary>
public sealed class KycRiskLevelChangedConsumer(
    CustomersDbContext db,
    IInboxGuard inbox,
    ILogger<KycRiskLevelChangedConsumer> logger)
    : IConsumer<KycRiskLevelChangedEvent>
{
    public async Task Consume(ConsumeContext<KycRiskLevelChangedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        // At-least-once delivery: a replay loses on the inbox primary key and exits.
        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId, evt.TenantId, nameof(KycRiskLevelChangedEvent), ct))
            return;

        // SYSTEM identity for the audit trail.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, LifecycleActors.System, "SYSTEM");

        // The level travels as a NAME so M02 never references this module's domain.
        // An unparsable value is logged and dropped rather than silently coerced to
        // Unknown, which would look like a downgrade of the client's risk.
        if (!Enum.TryParse<RiskLevel>(evt.RiskLevel, ignoreCase: true, out var riskLevel))
        {
            logger.LogWarning(
                "KycRiskLevelChangedEvent ignored: unknown risk level {RiskLevel} for client {ClientId} (tenant {TenantId}).",
                evt.RiskLevel, evt.CustomerEntityId, evt.TenantId);
            return;
        }

        var client = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == evt.TenantId && c.Id == evt.CustomerEntityId, ct);

        if (client is null)
        {
            // The guard has already committed the inbox row, so this event is
            // acknowledged and will not come back: a missing client means a stale or
            // bogus reference, logged for investigation.
            logger.LogWarning(
                "KycRiskLevelChangedEvent ignored: no client {ClientId} in tenant {TenantId}.",
                evt.CustomerEntityId, evt.TenantId);
            return;
        }

        client.ApplyRiskLevel(riskLevel, evt.ChangedAt);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Risk level of client {ClientId} (tenant {TenantId}) set to {RiskLevel}; status left at {Status}.",
            client.Id, evt.TenantId, riskLevel, client.Status);
    }
}
