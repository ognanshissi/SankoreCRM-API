namespace Sankore.Modules.Customers.Features.Holdings.Consumers;

using MassTransit;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Sync;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// The back office answered (BNF-B-16): the holding becomes <c>Active</c> with the reference it
/// gave.
///
/// <para>
/// It consumes the GENERIC success event rather than <c>PolicyIssuedEvent</c>, and the difference
/// matters: the policy event carries the insurer's own detail (a policy number, a premium) but is
/// keyed by customer and product, so it cannot say WHICH holding was accepted when a client holds
/// two on the same line. The generic one carries <c>EntityType</c> + <c>CrmId</c>, which for a
/// holding-driven subscription IS the holding id.
/// </para>
///
/// <para>
/// Filtered on <c>EntityType == "Policy"</c>, so a customer creation or a KYC level succeeding
/// passes through untouched. A <c>CrmId</c> that is not a holding of this tenant is simply not
/// found — which is also what a pre-BNF-B subscription keyed by customer looks like, and it is why
/// a miss here is information rather than an error.
/// </para>
/// </summary>
public sealed class HoldingSyncConfirmedConsumer(
    HoldingSyncService sync,
    IInboxGuard inbox,
    ILogger<HoldingSyncConfirmedConsumer> logger,
    IIntegrationModule? integration = null)
    : IConsumer<IntegrationCommandSucceededEvent>
{
    public async Task Consume(ConsumeContext<IntegrationCommandSucceededEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        // Policy only, and checked BEFORE the inbox guard: claiming an inbox row for every
        // successful command of every entity type would fill the table with rows this consumer
        // has nothing to do with.
        if (!string.Equals(evt.EntityType, "Policy", StringComparison.OrdinalIgnoreCase))
            return;

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(IntegrationCommandSucceededEvent),
                ct))
            return;

        // SYSTEM identity: what moved the holding is the insurer's answer, not a person.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");

        if (string.IsNullOrWhiteSpace(evt.ExternalRef))
        {
            // A success with no reference. The holding stays in PendingSync rather than going
            // Active without one: ExternalRef is the only link between the two systems, and an
            // Active holding without it cannot be reconciled (BNF-B-17) — which is a worse state
            // than one still visibly waiting.
            logger.LogWarning(
                "Integration command {CommandId} succeeded for holding {HoldingId} with no "
                + "external reference; the holding stays in PendingSync",
                evt.CommandId, evt.CrmId);
            return;
        }

        // An insurance contract sent before its premium (the insurer collects it, or the 60-day
        // derogation) is accepted but not in force: it waits « en attente de paiement » until the
        // premium is confirmed (insurance lot 2).
        var awaitingPayment = integration is not null
            && await integration.Insurance.IsPremiumOutstandingAsync(evt.TenantId, evt.CrmId, ct);

        var result = await sync.ConfirmAsync(
            evt.TenantId, evt.CrmId, evt.CommandType, evt.ExternalRef, ct, awaitingPayment);

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Holding {HoldingId} (tenant {TenantId}) is active with external reference {Ref}",
                evt.CrmId, evt.TenantId, evt.ExternalRef);
            return;
        }

        if (result.Error == CustomerErrors.HoldingNotFound)
        {
            // Expected for a subscription keyed by customer rather than by holding — every one
            // issued before BNF-B-16 — so information, not a warning.
            logger.LogInformation(
                "Integration success for CRM id {CrmId} matched no holding in tenant {TenantId}",
                evt.CrmId, evt.TenantId);
            return;
        }

        logger.LogWarning(
            "Holding {HoldingId} (tenant {TenantId}) could not be confirmed: {Error}",
            evt.CrmId, evt.TenantId, result.Error);
    }
}

/// <summary>
/// The back office refused it (BNF-B-16): the holding becomes <c>Rejected</c> and carries a reason
/// an agent can act on.
///
/// <para>
/// The reason is built from the error FAMILY and CODE the far end gave, not from a free-text
/// detail: <c>Technical</c> means the institution has something to fix and the subscription should
/// be re-sent, <c>Functional</c> means the insurer refused on the merits and the file needs
/// changing. An agent reading the holding has to be able to tell those apart without opening the
/// integration console.
/// </para>
/// </summary>
public sealed class HoldingSyncRejectedConsumer(
    HoldingSyncService sync,
    IInboxGuard inbox,
    ILogger<HoldingSyncRejectedConsumer> logger)
    : IConsumer<IntegrationCommandRejectedEvent>
{
    public async Task Consume(ConsumeContext<IntegrationCommandRejectedEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        if (!string.Equals(evt.EntityType, "Policy", StringComparison.OrdinalIgnoreCase))
            return;

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(IntegrationCommandRejectedEvent),
                ct))
            return;

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");

        var reason = Reason(evt);
        var result = await sync.RejectAsync(evt.TenantId, evt.CrmId, reason, ct);

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Holding {HoldingId} (tenant {TenantId}) was rejected by the back office: {Reason}",
                evt.CrmId, evt.TenantId, reason);
            return;
        }

        if (result.Error == CustomerErrors.HoldingNotFound)
        {
            logger.LogInformation(
                "Integration rejection for CRM id {CrmId} matched no holding in tenant {TenantId}",
                evt.CrmId, evt.TenantId);
            return;
        }

        logger.LogWarning(
            "Holding {HoldingId} (tenant {TenantId}) could not be marked rejected: {Error}",
            evt.CrmId, evt.TenantId, result.Error);
    }

    /// <summary>
    /// A reason an agent can act on, bounded to the column's width.
    /// </summary>
    /// <remarks>
    /// The detail is TRUNCATED rather than dropped: an adapter's message can be a stack trace or a
    /// whole SOAP fault, and <c>rejection_reason</c> holds 1000 characters — but the first line of
    /// an insurer's refusal is usually the part that says why.
    /// </remarks>
    private static string Reason(IntegrationCommandRejectedEvent evt)
    {
        var head = $"{evt.ErrorFamily}/{evt.ErrorCode}";

        if (string.IsNullOrWhiteSpace(evt.Detail))
            return head;

        var detail = evt.Detail.Trim();
        const int room = 900;

        return detail.Length <= room
            ? $"{head}: {detail}"
            : $"{head}: {detail[..room]}…";
    }
}
