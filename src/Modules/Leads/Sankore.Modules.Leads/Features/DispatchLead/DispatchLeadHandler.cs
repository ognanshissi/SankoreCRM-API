namespace Sankore.Modules.Leads.Features.DispatchLead;

using System.Text.Json;
using System.Transactions;
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
using Sankore.Shared.Infrastructure.Compliance;
using Sankore.Shared.Infrastructure.Auth;
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
    ClientAdvisorResolver clientAdvisorResolver,
    InsuranceDistributionGuard distributionGuard,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    ILogger<DispatchLeadHandler> logger,
    TimeProvider clock,
    ICurrentUser? currentUser = null)
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
        var candidates = await AgentLoad.WithLiveLoadAsync(db, cmd.TenantId,
            await usersModule.GetAvailableAgentsAsync(
                tenantId: cmd.TenantId,
                agencyId: lead.PreferredAgencyId,
                ct: ct),
            ct);

        if (candidates.Count == 0)
        {
            await QueueForRetryAsync(cmd, lead, () => publisher.PublishAsync(
                new LeadDispatchingFailedEvent(lead.Id, cmd.TenantId, "NO_AGENT_AVAILABLE"), ct), ct);

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
                    SlaDeadline: manualAssignment.SlaDeadline,
                    // Guid.Empty is the SYSTEM identity of a background caller, not a person.
                    ActorId: currentUser?.Id is { } actor && actor != Guid.Empty ? actor : null,
                    Reason: cmd.OverrideReason),
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

        // 4. Filter out permanently excluded agents before strategy evaluation — and the ones this
        //    dispatch was asked to skip (a takeover-SLA redispatch, WA-BE-15).
        var excluded = cmd.ExcludedAgentIds is { Count: > 0 } skip
            ? rules.ExcludedAgentIds.Concat(skip).ToHashSet()
            : rules.ExcludedAgentIds.ToHashSet();

        var eligible_candidates = excluded.Count > 0
            ? candidates.Where(a => !excluded.Contains(a.Id)).ToList()
            : candidates;

        if (eligible_candidates.Count == 0)
        {
            await QueueForRetryAsync(cmd, lead, () => publisher.PublishAsync(
                new LeadDispatchingFailedEvent(lead.Id, cmd.TenantId, "NO_AGENT_AVAILABLE_AFTER_EXCLUSIONS"), ct), ct);
            return Result.Fail<DispatchLeadResult>("NO_AGENT_AVAILABLE");
        }

        // 4a. CIMA article 503: an insurance operation may be presented at an IMF's counters only
        //     by somebody holding the professional card of article 510. On an INSURANCE product
        //     line, agents without a valid card leave the pool.
        //
        //     Placed here — after the administrator's exclusion list, before every heuristic —
        //     because it is not a preference being weighed. An unlicensed agent is not a worse
        //     candidate, they are not a candidate: the dispatch would park a lead on somebody who
        //     cannot lawfully work it, and the saturation and compatibility stages below would
        //     spend their effort ranking them.
        //
        //     Costs nothing on a non-insurance line (one cached catalogue read) and nothing at all
        //     on a lead whose product line is unresolved, which is most of them at capture. On an
        //     M12 outage the pool is left unchanged — see the guard, which explains why this side
        //     of the asymmetry is the permissive one.
        var licensed = await distributionGuard.FilterEligibleAsync(
            cmd.TenantId,
            [.. eligible_candidates.Select(a => a.Id)],
            lead.ProductLineId,
            ct);

        if (licensed.Count != eligible_candidates.Count)
        {
            var licensedIds = licensed.ToHashSet();
            eligible_candidates = eligible_candidates.Where(a => licensedIds.Contains(a.Id)).ToList();

            if (eligible_candidates.Count == 0)
            {
                // Its own reason code. "No agent available" on an insurance lead whose whole
                // agency happens to hold no card is not a capacity problem, and telling an
                // operator it is would send them to the wrong screen entirely — the one they
                // need is the credential register.
                await QueueForRetryAsync(cmd, lead, () => publisher.PublishAsync(
                    new LeadDispatchingFailedEvent(
                        lead.Id, cmd.TenantId, "NO_LICENSED_AGENT_AVAILABLE"), ct), ct);

                return Result.Fail<DispatchLeadResult>("NO_LICENSED_AGENT_AVAILABLE");
            }
        }

        // 4b. Client history (F13.10 / BNF-B-22): a request from a customer already on file goes
        //     to that customer's own advisor. The relationship is worth more than a balanced
        //     queue — the client expects the person they know, and that person has the context.
        //
        //     Placed AFTER the exclusion list (an administrator saying "not this one" still binds)
        //     and BEFORE the load heuristics, which are deliberately skipped: MaxTasksPerAgent and
        //     the anti-monopoly threshold exist to shape how the engine spreads work around, and
        //     "this client's advisor" is not the engine spreading work around. The same reasoning
        //     the explicit-agent override above already applies.
        //
        //     It is a PREFERENCE, not a requirement: an unavailable advisor falls through to the
        //     configured strategy rather than failing the dispatch.
        var clientAdvisor = await clientAdvisorResolver.ResolveAsync(lead, eligible_candidates, ct);

        if (clientAdvisor is not null)
        {
            var advisorRules = rules;

            var advisorAssignment = LeadAssignment.Create(
                tenantId: cmd.TenantId,
                leadId: lead.Id,
                agentId: clientAdvisor.Id,
                strategy: DispatchingStrategy.StickyAssignment,
                compatibilityScore: 100,
                slaDeadline: clock.GetUtcNow().Add(advisorRules.FirstContactSla),
                createdAt: clock.GetUtcNow(),
                // Same convention as the strategy path below: Guid.Empty is the built-in default
                // rule, which has no row, so the assignment records null rather than a dangling id.
                ruleId: advisorRules.Id == Guid.Empty ? null : advisorRules.Id);

            var advisorResult = lead.AssignTo(advisorAssignment, outgoingAssignment);
            if (advisorResult.IsFailure)
                return Result.Fail<DispatchLeadResult>(advisorResult.Error!);

            db.LeadAssignments.Add(advisorAssignment);

            await publisher.PublishAsync(
                new LeadDispatchedEvent(
                    LeadId: lead.Id,
                    TenantId: cmd.TenantId,
                    AgentId: clientAdvisor.Id,
                    Strategy: advisorAssignment.Strategy,
                    Score: advisorAssignment.CompatibilityScore,
                    SlaDeadline: advisorAssignment.SlaDeadline),
                ct);

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Lead {LeadId} routed to customer {CustomerId}'s own advisor {AgentId} "
                + "(client-history criterion)",
                lead.Id, lead.CustomerId, clientAdvisor.Id);

            return Result.Ok(new DispatchLeadResult(
                AssignmentId: advisorAssignment.Id,
                AgentId: clientAdvisor.Id,
                AgentName: clientAdvisor.FullName,
                CompatibilityScore: advisorAssignment.CompatibilityScore,
                SlaDeadline: advisorAssignment.SlaDeadline));
        }

        // 5a. Saturation filter (US-M13-082): exclude agents whose open CRM task
        //     count has already reached MaxTasksPerAgent. Capacity reads are served
        //     from the short-TTL Redis cache maintained by AgentCapacityService.
        var unsaturated = new List<AgentSummary>(eligible_candidates.Count);
        foreach (var agent in eligible_candidates)
        {
            var openTasks = await capacityService.GetOpenTaskCountAsync(cmd.TenantId, agent.Id, ct);
            if (rules.HasTaskCapacityFor(openTasks))
                unsaturated.Add(agent);
        }

        if (unsaturated.Count == 0)
        {
            await QueueForRetryAsync(cmd, lead, () => publisher.PublishAsync(
                new LeadDispatchingFailedEvent(lead.Id, cmd.TenantId, "ALL_AGENTS_AT_TASK_CAPACITY"), ct), ct);
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
            // The lead is left without an agent all the same, so it is a dispatch failure too: the
            // anti-monopoly signal alone reaches nobody who can place the lead.
            await QueueForRetryAsync(cmd, lead, async () =>
            {
                await publisher.PublishAsync(
                    new AntiMonopolyTriggeredEvent(lead.Id, rules.AntiMonopolyThreshold), ct);
                await publisher.PublishAsync(
                    new LeadDispatchingFailedEvent(lead.Id, cmd.TenantId, "ANTI_MONOPOLY_BLOCKED"), ct);
            }, ct);

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
            // Serialized here, at the edge: the column is an audit record of what was
            // computed, and keeping it a string leaves existing rows readable.
            compatibilityFactorsJson: JsonSerializer.Serialize(winner.Factors),
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

    /// <summary>
    /// Puts the lead in the dispatch queue and announces it — once. <c>DispatchWaitingLeadsJob</c>
    /// retries it until an agent becomes available; a retry that fails again finds it already
    /// queued and stays silent, so a lead waiting an afternoon does not raise an alert every few
    /// minutes.
    ///
    /// <para>
    /// Committed on its own, in a suppressed transaction scope. The failure events used to be
    /// written to the outbox and then dropped: the handler returns <c>Result.Fail</c> without
    /// saving, and <c>TransactionBehavior</c> rolls the ambient scope back on a failure anyway. No
    /// <c>LeadDispatchingFailedEvent</c> ever left this handler — an import whose ten leads all
    /// failed to dispatch left no task and no trace, and the leads sat unassigned for good.
    /// Suppressing gives the queue entry and its events their own short transaction (as
    /// <c>RevealSensitiveFieldHandler</c> does for its alert): the dispatch is still refused, the
    /// record of the refusal survives.
    /// </para>
    ///
    /// Safe to save the whole context: every failure path returns before the lead's assignment or
    /// the outgoing assignment is touched, so the queue mark and the outbox rows are the only
    /// pending changes.
    /// </summary>
    private async Task QueueForRetryAsync(DispatchLeadCommand cmd, Lead lead, Func<Task> publish, CancellationToken ct)
    {
        if (!cmd.QueueWhenNoAgent)
            return;

        if (!lead.MarkAwaitingDispatch(clock.GetUtcNow()))
            return;

        using var suppressed = new TransactionScope(
            TransactionScopeOption.Suppress,
            TransactionScopeAsyncFlowOption.Enabled);

        await publish();
        await db.SaveChangesAsync(ct);
    }
}
