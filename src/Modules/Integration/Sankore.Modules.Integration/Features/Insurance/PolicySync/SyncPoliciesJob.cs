namespace Sankore.Modules.Integration.Features.Insurance.PolicySync;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Ports;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// ASS-07: brings the <c>ins_policy</c> read model into line with what each insurer actually
/// holds, and announces a status change — once.
///
/// <para>
/// <b>Why a read model at all.</b> <c>IInsuranceGateway.GetPoliciesAsync</c> already asks the
/// insurer live, which is right for a 360 screen somebody is looking at. It is wrong for
/// everything else: a reconciliation run (BNF-B-17) compares against this table, a statement
/// (ASS-10) reads it, and neither can call an insurer's API per row. A policy sold at the
/// insurer's own counter — never subscribed through SANKORE, so carrying no saga — exists for this
/// platform only once this job has seen it.
/// </para>
///
/// <para>
/// <b>The event is published only when the status MOVED.</b> That is <c>ApplySync</c>'s return
/// value and the reason it has one: a nightly sweep over a portfolio would otherwise publish one
/// event per policy per night, which M08 turns into a notification to every customer, every
/// night, about nothing.
/// </para>
///
/// <para>
/// <b>Per connection, not per tenant.</b> A tenant has one core banking system but may distribute
/// for several insurers, each with its own adapter and its own view of its own policies — the
/// asymmetry the whole insurance family is built around.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type.
public sealed class SyncPoliciesJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncPoliciesJob> logger)
{
    /// <summary>
    /// Customers per page. The external ids become one SQL <c>IN</c> and one adapter call each,
    /// so the page bounds both — the same caveat the reconciliation run carries.
    /// </summary>
    private const int CustomerPageSize = 200;

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        // SYSTEM identity, established BEFORE the DI scope: ITenantContext and ICurrentUser are
        // built from it and a Hangfire job has no HTTP context.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var resolver = sp.GetRequiredService<IntegrationAdapterResolver>();
        var publisher = sp.GetRequiredService<IEventPublisher>();
        var clock = sp.GetRequiredService<TimeProvider>();

        // IgnoreQueryFilters with an explicit tenant predicate: a Hangfire job runs outside any
        // HTTP context, so the ambient tenant cannot be trusted to be the one being processed.
        var connections = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.Family == IntegrationFamily.Insurance
                     && c.IsActive)
            .ToListAsync(ct);

        if (connections.Count == 0) return;

        var seen = 0;
        var moved = 0;

        foreach (var connection in connections)
        {
            var resolved = resolver.ResolveFor(connection);

            if (resolved.IsFailure)
            {
                // No adapter for this kind, or none registered. Not this job's problem to fix and
                // not a reason to abandon the other insurers of the tenant.
                logger.LogWarning(
                    "Policy sync skipped connection {ConnectionId} ({Name}): {Error}",
                    connection.Id, connection.Name, resolved.Code);
                continue;
            }

            var port = resolver.ResolvePort<IInsurancePolicyPort>(
                resolved.Value, IntegrationCapability.ReadPolicies);

            if (port.IsFailure)
            {
                // A bordereau-fed insurer declares no read capability at all: its policies arrive
                // by file through the batch chain, and asking it for them would be asking the
                // wrong question. Debug, not a warning — this is a configuration, not a fault.
                logger.LogDebug(
                    "Connection {ConnectionId} does not support ReadPolicies; its policies arrive "
                    + "by batch; {Code}", connection.Id, port.Code);
                continue;
            }

            var outcome = await SyncConnectionAsync(
                db, publisher, clock, port.Value, connection, tenantId, ct);

            seen += outcome.Seen;
            moved += outcome.Moved;
        }

        if (seen > 0)
        {
            logger.LogInformation(
                "Policy sync for tenant {TenantId}: {Seen} policy(ies) across {Connections} "
                + "insurer(s), {Moved} status change(s) announced",
                tenantId, seen, connections.Count, moved);
        }
    }

    private async Task<(int Seen, int Moved)> SyncConnectionAsync(
        IntegrationDbContext db,
        IEventPublisher publisher,
        TimeProvider clock,
        IInsurancePolicyPort port,
        IntegrationConnection connection,
        Guid tenantId,
        CancellationToken ct)
    {
        var seen = 0;
        var moved = 0;
        var page = 0;

        while (true)
        {
            // The customers this insurer knows, through the reference table — the only route that
            // exists, exactly as the reconciliation run enumerates its own side. A customer with
            // no reference at this insurer has no policies there to read.
            var customers = await db.References
                .IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId
                         && r.ConnectionId == connection.Id
                         && r.EntityType == IntegrationEntityTypes.Customer)
                .OrderBy(r => r.CrmId)
                .Skip(page * CustomerPageSize)
                .Take(CustomerPageSize)
                .Select(r => new { r.CrmId, r.ExternalId })
                .ToListAsync(ct);

            if (customers.Count == 0) break;

            foreach (var customer in customers)
            {
                var policies = await port.GetPoliciesAsync(new ExternalId(customer.ExternalId), ct);

                if (policies.IsFailure)
                {
                    // One customer's read failing must not abandon the rest of the portfolio:
                    // tomorrow's run reads them again, and a sync that stops at the first timeout
                    // never finishes a night.
                    logger.LogWarning(
                        "Policy sync could not read customer {CrmId} at connection "
                        + "{ConnectionId}: {Error}",
                        customer.CrmId, connection.Id, policies.Code);
                    continue;
                }

                foreach (var policy in policies.Value)
                {
                    seen++;

                    if (await ApplyAsync(
                            db, publisher, clock, connection, tenantId,
                            customer.CrmId, policy, ct))
                    {
                        moved++;
                    }
                }
            }

            // Saved per page: an interrupted night keeps the pages it finished rather than
            // discarding the whole sweep.
            await db.SaveChangesAsync(ct);

            if (customers.Count < CustomerPageSize) break;
            page++;
        }

        return (seen, moved);
    }

    /// <summary>
    /// Upserts one policy. Returns whether its status MOVED, which is the only case an event is
    /// published for.
    /// </summary>
    private async Task<bool> ApplyAsync(
        IntegrationDbContext db,
        IEventPublisher publisher,
        TimeProvider clock,
        IntegrationConnection connection,
        Guid tenantId,
        Guid crmCustomerId,
        InsurancePolicy incoming,
        CancellationToken ct)
    {
        var existing = await db.Policies
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId
                  && p.ConnectionId == connection.Id
                  && p.ExternalPolicyId == incoming.PolicyId.Value,
                ct);

        if (existing is null)
        {
            // First sight. A policy sold at the insurer's own counter arrives here, which is why
            // this job exists at all — and no event is published for it: discovering a policy that
            // has been running for a year is not a status change, and announcing one would tell
            // the customer their contract just became active.
            db.Policies.Add(PolicyRecord.Create(
                tenantId: tenantId,
                connectionId: connection.Id,
                crmCustomerId: crmCustomerId,
                externalPolicyId: incoming.PolicyId.Value,
                policyNumber: incoming.PolicyNumber,
                insurerProductCode: incoming.InsurerProductCode,
                status: incoming.Status,
                effectiveDate: incoming.EffectiveDate,
                premiumAmount: incoming.PremiumAmount,
                currency: incoming.Currency,
                periodicity: incoming.Periodicity,
                clock: clock,
                expiryDate: incoming.ExpiryDate,
                nextDueDate: incoming.NextDueDate));

            return false;
        }

        var previous = existing.Status;

        var statusMoved = existing.ApplySync(
            status: incoming.Status,
            policyNumber: incoming.PolicyNumber,
            effectiveDate: incoming.EffectiveDate,
            expiryDate: incoming.ExpiryDate,
            nextDueDate: incoming.NextDueDate,
            premiumAmount: incoming.PremiumAmount,
            periodicity: incoming.Periodicity,
            clock: clock);

        if (!statusMoved) return false;

        // Through the outbox, in the same transaction as the row that moved: an event without its
        // row would tell a customer about a change this platform does not believe happened.
        await publisher.PublishAsync(
            new PolicyStatusChangedEvent(
                TenantId: tenantId,
                CrmCustomerId: crmCustomerId,
                ExternalPolicyId: existing.ExternalPolicyId,
                PreviousStatus: previous.ToString(),
                CurrentStatus: incoming.Status.ToString(),
                // The insurer's own reason, when it gave one. Never a value this platform invents:
                // an agent reads it, and a guess reads as fact.
                Reason: existing.StatusDetail,
                ChangedAt: clock.GetUtcNow()),
            ct);

        return true;
    }
}
