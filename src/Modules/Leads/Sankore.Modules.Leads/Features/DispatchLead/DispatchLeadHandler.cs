namespace Sankore.Modules.Leads.Features.DispatchLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
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
    IAdministrationModule usersModule,
    CompatibilityScorer scorer,
    DispatchingStrategyFactory strategyFactory,
    DispatchingRuleResolver ruleResolver,
    AgentCapacityService capacityService,
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
        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<DispatchLeadResult>("LEAD_NOT_FOUND");

        // Any live lead may be dispatched — a captured lead is routed to an agent precisely so
        // that someone qualifies it. Only the terminal statuses refuse; Lead.IsDispatchable is
        // the single definition, shared with Lead.AssignTo.
        if (!lead.IsDispatchable)
            return Result.Fail<DispatchLeadResult>("LEAD_NOT_DISPATCHABLE");

        // The assignment being replaced, if any. Loaded once for both paths below: the aggregate
        // closes it (LeadAssignment.Supersede) and cannot reach it by itself — there is no
        // navigation from Lead to its assignments. AsTracking, since it is about to be mutated.
        var outgoingAssignment = lead.CurrentAssignmentId is { } currentId
            ? await db.LeadAssignments.AsTracking().FirstOrDefaultAsync(a => a.Id == currentId, ct)
            : null;

        // 2. Load available agents from the Users module (cross-module contract — PublicApi only)
        var candidates = await usersModule.GetAvailableAgentsAsync(
            tenantId: cmd.TenantId,
            agencyId: lead.PreferredAgencyId,
            ct: ct);

        if (candidates.Count == 0)
        {
            await publisher.PublishAsync(
                new LeadDispatchingFailedEvent(lead.Id, cmd.TenantId, "NO_AGENT_AVAILABLE"), ct);

            logger.LogWarning("Lead {LeadId} could not be dispatched: no agent available", lead.Id);
            return Result.Fail<DispatchLeadResult>("NO_AGENT_AVAILABLE");
        }

        // 3. Resolve the applicable rule: the one pinned on the lead's source, else the
        //    highest-priority active rule, else the built-in defaults. When the caller named a
        //    strategy, the rule tuned for that strategy is used instead — see the resolver.
        var rules = await ruleResolver.ResolveAsync(lead, cmd.Strategy, ct);

        // The rule carries the strategy; an explicit request still wins.
        var effectiveStrategy = cmd.Strategy ?? rules.Strategy;

        // 3b. Explicit agent: the supervisor override. Everything that says WHETHER this agent
        //     may receive leads still applies — the Users module must list them as available for
        //     the lead's agency (step 2 above), and the rule's exclusion list still binds, since
        //     that list is an administrator saying "not this one" and a caller should not be able
        //     to walk past it by naming them.
        //
        //     What is deliberately skipped: the ranking (there is nothing to rank), and the two
        //     LOAD heuristics — MaxTasksPerAgent and the anti-monopoly threshold. Those exist to
        //     shape how the ENGINE spreads leads around; vetoing a named human decision is not
        //     their job, and a supervisor told "ANTI_MONOPOLY_BLOCKED" after choosing a specific
        //     person has no way to act on it. The bypass is not silent: the assignment is written
        //     through CreateManualOverride with WasManualOverride = true and the caller's reason,
        //     which GetAssignmentHistory already shows, and it is logged as a warning. It is also
        //     self-correcting for capacity — the agent is then over MaxTasksPerAgent, so the
        //     saturation filter in step 5a excludes them from automatic dispatch until they are
        //     back under it.
        if (cmd.AgentId is { } explicitAgentId)
        {
            var named = candidates.FirstOrDefault(a => a.Id == explicitAgentId);

            if (named is null)
            {
                // Covers every "may not receive this lead" case the Users module encodes: unknown
                // id, inactive account, wrong agency for the lead, not available. Reported as one
                // code because the pool is a cross-module contract and the reason is not ours.
                logger.LogWarning(
                    "Lead {LeadId}: explicit agent {AgentId} is not an available agent for agency {AgencyId}",
                    lead.Id, explicitAgentId, lead.PreferredAgencyId);
                return Result.Fail<DispatchLeadResult>("AGENT_NOT_ELIGIBLE");
            }

            var explicitRules = await ruleResolver.ResolveAsync(lead, requestedStrategy: null, ct);

            if (explicitRules.ExcludedAgentIds.Contains(explicitAgentId))
            {
                logger.LogWarning(
                    "Lead {LeadId}: explicit agent {AgentId} is on the rule's exclusion list",
                    lead.Id, explicitAgentId);
                return Result.Fail<DispatchLeadResult>("AGENT_EXCLUDED_BY_RULE");
            }

            var manualAssignment = LeadAssignment.CreateManualOverride(
                tenantId: cmd.TenantId,
                leadId: lead.Id,
                agentId: named.Id,
                reason: cmd.OverrideReason!,
                slaDeadline: clock.GetUtcNow().Add(explicitRules.FirstContactSla),
                createdAt: clock.GetUtcNow());

            var manualResult = lead.AssignTo(manualAssignment, outgoingAssignment);
            if (manualResult.IsFailure)
                return Result.Fail<DispatchLeadResult>(manualResult.Error!);

            db.LeadAssignments.Add(manualAssignment);

            await publisher.PublishAsync(
                new LeadDispatchedEvent(
                    LeadId: lead.Id,
                    TenantId: cmd.TenantId,
                    AgentId: named.Id,
                    // The strategy the row records for an override — CreateManualOverride's own
                    // choice — is a placeholder, not a claim that scoring ran.
                    Strategy: manualAssignment.Strategy,
                    Score: manualAssignment.CompatibilityScore,
                    SlaDeadline: manualAssignment.SlaDeadline),
                ct);

            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Lead {LeadId} assigned to agent {AgentId} by explicit override (reason: {Reason}); "
                + "ranking, task capacity and anti-monopoly were not applied",
                lead.Id, named.Id, cmd.OverrideReason);

            return Result.Ok(new DispatchLeadResult(
                AssignmentId: manualAssignment.Id,
                AgentId: named.Id,
                AgentName: named.FullName,
                CompatibilityScore: manualAssignment.CompatibilityScore,
                SlaDeadline: manualAssignment.SlaDeadline));
        }

        // 4. Filter out permanently excluded agents before strategy evaluation.
        var eligible_candidates = rules.ExcludedAgentIds.Count > 0
            ? candidates.Where(a => !rules.ExcludedAgentIds.Contains(a.Id)).ToList()
            : candidates;

        if (eligible_candidates.Count == 0)
        {
            await publisher.PublishAsync(
                new LeadDispatchingFailedEvent(lead.Id, cmd.TenantId, "NO_AGENT_AVAILABLE_AFTER_EXCLUSIONS"), ct);
            return Result.Fail<DispatchLeadResult>("NO_AGENT_AVAILABLE");
        }

        // 5a. Saturation filter (US-M13-082): exclude agents whose open CRM task
        //     count has already reached MaxTasksPerAgent. Capacity reads are served
        //     from the short-TTL Redis cache maintained by AgentCapacityService.
        var unsaturated = new List<AgentSummary>(eligible_candidates.Count);
        foreach (var agent in eligible_candidates)
        {
            var openTasks = await capacityService.GetOpenTaskCountAsync(cmd.TenantId, agent.Id, ct);
            if (openTasks < rules.MaxTasksPerAgent)
                unsaturated.Add(agent);
        }

        if (unsaturated.Count == 0)
        {
            await publisher.PublishAsync(
                new LeadDispatchingFailedEvent(lead.Id, cmd.TenantId, "ALL_AGENTS_AT_TASK_CAPACITY"), ct);
            logger.LogWarning(
                "Lead {LeadId} blocked: all {Count} eligible agents are at task capacity ({Max})",
                lead.Id, eligible_candidates.Count, rules.MaxTasksPerAgent);
            return Result.Fail<DispatchLeadResult>("ALL_AGENTS_AT_TASK_CAPACITY");
        }

        // 5. Apply the selected strategy to rank candidates
        var strategy = strategyFactory.Create(effectiveStrategy);
        var scored = await strategy.EvaluateAsync(lead, unsaturated, rules, scorer, ct);

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
            strategy: effectiveStrategy,
            compatibilityScore: winner.CompatibilityScore,
            slaDeadline: clock.GetUtcNow().Add(rules.FirstContactSla),
            createdAt: clock.GetUtcNow(),
            compatibilityFactorsJson: winner.FactorsJson,
            // Guid.Empty is DispatchingRule.Default()'s id: record "no configured rule" as
            // null rather than as an id that matches no row.
            ruleId: rules.Id == Guid.Empty ? null : rules.Id);

        var assignResult = lead.AssignTo(assignment, outgoingAssignment);
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
                TenantId: cmd.TenantId,
                AgentId: winner.Agent.Id,
                Strategy: effectiveStrategy,
                Score: winner.CompatibilityScore,
                SlaDeadline: assignment.SlaDeadline),
            ct);

        // 8. Single atomic commit: lead status change + assignment row +
        //    outbox row all persist together, or none do (wrapped further
        //    by the ambient TransactionScope from TransactionBehavior).
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Lead {LeadId} dispatched to agent {AgentId} (score={Score}, strategy={Strategy})",
            lead.Id, winner.Agent.Id, winner.CompatibilityScore, effectiveStrategy);

        return Result.Ok(new DispatchLeadResult(
            AssignmentId: assignment.Id,
            AgentId: winner.Agent.Id,
            AgentName: winner.Agent.FullName,
            CompatibilityScore: winner.CompatibilityScore,
            SlaDeadline: assignment.SlaDeadline));
    }
}
