namespace Sankore.Modules.Leads.Features.DispatchLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Users.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Orchestrates F13.9/F13.10: load the lead, fetch available agents from
/// the Users module (via its PublicApi only), score and rank them, apply
/// the anti-monopoly filter, persist the assignment, and publish the
/// resulting integration event — all inside a single transaction (via
/// TransactionBehavior wrapping this handler because DispatchLeadCommand
/// implements ICommand).
/// </summary>
internal sealed class DispatchLeadHandler(
    LeadsDbContext db,
    IUsersModule usersModule,
    CompatibilityScorer scorer,
    DispatchingStrategyFactory strategyFactory,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    ILogger<DispatchLeadHandler> logger,
    TimeProvider clock)
    : IRequestHandler<DispatchLeadCommand, Result<DispatchLeadResult>>
{
    public async Task<Result<DispatchLeadResult>> Handle(
        DispatchLeadCommand cmd,
        CancellationToken ct)
    {
        // 1. Load lead (tenant-scoped automatically by the DbContext's global query filter)
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<DispatchLeadResult>("LEAD_NOT_FOUND");

        if (lead.Status != LeadStatus.SalesQualified)
            return Result.Fail<DispatchLeadResult>("LEAD_NOT_QUALIFIED");

        // 2. Load available agents from the Users module (cross-module contract — PublicApi only)
        var candidates = await usersModule.GetAvailableAgentsAsync(
            tenantId: cmd.TenantId,
            agencyId: lead.PreferredAgencyId,
            ct: ct);

        if (candidates.Count == 0)
        {
            await publisher.PublishAsync(
                new LeadDispatchingFailedEvent(lead.Id, "NO_AGENT_AVAILABLE"), ct);

            logger.LogWarning("Lead {LeadId} could not be dispatched: no agent available", lead.Id);
            return Result.Fail<DispatchLeadResult>("NO_AGENT_AVAILABLE");
        }

        // 3. Load tenant-specific dispatching rules (fallback to sane defaults)
        var rules = await db.DispatchingRules
            .Where(r => r.IsActive && r.Strategy == cmd.Strategy)
            .SingleOrDefaultAsync(ct)
            ?? DispatchingRule.Default();

        // 4. Apply the selected strategy to rank candidates
        var strategy = strategyFactory.Create(cmd.Strategy);
        var scored = await strategy.EvaluateAsync(lead, candidates, rules, scorer, ct);

        // 5. Apply the anti-monopoly filter (F13.15)
        var eligible = scored
            .Where(s => s.Agent.HotLeadsCount < rules.AntiMonopolyThreshold)
            .OrderByDescending(s => s.CompatibilityScore)
            .ToList();

        if (eligible.Count == 0)
        {
            await publisher.PublishAsync(
                new AntiMonopolyTriggeredEvent(lead.Id, rules.AntiMonopolyThreshold), ct);

            logger.LogWarning(
                "Lead {LeadId} blocked by anti-monopoly threshold ({Threshold})",
                lead.Id, rules.AntiMonopolyThreshold);
            return Result.Fail<DispatchLeadResult>("ANTI_MONOPOLY_BLOCKED");
        }

        var winner = eligible.First();

        // 6. Create the assignment and mutate the aggregate through its own invariants
        var assignment = LeadAssignment.Create(
            tenantId: cmd.TenantId,
            leadId: lead.Id,
            agentId: winner.Agent.Id,
            strategy: cmd.Strategy,
            compatibilityScore: winner.CompatibilityScore,
            slaDeadline: clock.GetUtcNow().Add(rules.FirstContactSla),
            createdAt: clock.GetUtcNow());

        var assignResult = lead.AssignTo(assignment);
        if (assignResult.IsFailure)
            return Result.Fail<DispatchLeadResult>(assignResult.Error!);

        db.LeadAssignments.Add(assignment);

        // 7. Publish the integration event: OutboxEventPublisher writes the
        //    row into THIS SAME DbContext instance (no SaveChanges inside
        //    it — see OutboxEventPublisher<TDbContext>), so it lands in the
        //    same transaction as the lead + assignment change below.
        await publisher.PublishAsync(
            new LeadDispatchedEvent(
                LeadId: lead.Id,
                AgentId: winner.Agent.Id,
                Strategy: cmd.Strategy,
                Score: winner.CompatibilityScore,
                SlaDeadline: assignment.SlaDeadline),
            ct);

        // 8. Single atomic commit: lead status change + assignment row +
        //    outbox row all persist together, or none do (wrapped further
        //    by the ambient TransactionScope from TransactionBehavior).
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Lead {LeadId} dispatched to agent {AgentId} (score={Score}, strategy={Strategy})",
            lead.Id, winner.Agent.Id, winner.CompatibilityScore, cmd.Strategy);

        return Result.Ok(new DispatchLeadResult(
            AssignmentId: assignment.Id,
            AgentId: winner.Agent.Id,
            AgentName: winner.Agent.FullName,
            CompatibilityScore: winner.CompatibilityScore,
            SlaDeadline: assignment.SlaDeadline));
    }
}
