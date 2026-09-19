namespace Sankore.Modules.Leads.Features.Tasks.DispatchTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Orchestrates US-M13-081: load the task, resolve its lead context, fetch
/// available agents, score them with the shared CompatibilityScorer (US-M13-072),
/// apply the anti-monopoly filter, persist the scored assignment on the task, and
/// save — all inside a single transaction via TransactionBehavior.
///
/// Requires the task to be linked to a lead (LeadId != null) so that all six
/// scoring factors (language, product, geography, workload, performance, agency)
/// can be evaluated with the same explainability as lead dispatch.
/// Use the manual AssignTask command for tasks not tied to a lead.
/// </summary>
internal sealed class DispatchTaskHandler(
    LeadsDbContext db,
    IAdministrationModule usersModule,
    CompatibilityScorer scorer,
    DispatchingStrategyFactory strategyFactory,
    AgentCapacityService capacityService,
    ILogger<DispatchTaskHandler> logger)
    : IRequestHandler<DispatchTaskCommand, Result<DispatchTaskResult>>
{
    public async Task<Result<DispatchTaskResult>> Handle(
        DispatchTaskCommand cmd, CancellationToken ct)
    {
        // 1. Load the task (tenant-scoped by global query filter)
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail<DispatchTaskResult>("TASK_NOT_FOUND");

        if (task.Status == CrmTaskStatus.Completed)
            return Result.Fail<DispatchTaskResult>("TASK_ALREADY_COMPLETED");

        if (task.Status == CrmTaskStatus.Cancelled)
            return Result.Fail<DispatchTaskResult>("TASK_CANCELLED");

        // 2. Require lead context — scorer needs all six factors
        if (task.LeadId is null)
            return Result.Fail<DispatchTaskResult>("TASK_HAS_NO_LEAD_CONTEXT");

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == task.LeadId, ct);
        if (lead is null)
            return Result.Fail<DispatchTaskResult>("LEAD_NOT_FOUND");

        // 3. Fetch available agents from the Administration module (PublicApi only)
        var candidates = await usersModule.GetAvailableAgentsAsync(
            tenantId: cmd.TenantId,
            agencyId: lead.PreferredAgencyId,
            ct: ct);

        if (candidates.Count == 0)
        {
            logger.LogWarning(
                "Task {TaskId} could not be dispatched: no agent available (tenant {TenantId})",
                task.Id, cmd.TenantId);
            return Result.Fail<DispatchTaskResult>("NO_AGENT_AVAILABLE");
        }

        // 4. Load tenant dispatching rules (fallback to safe defaults)
        var rules = await db.DispatchingRules
            .Where(r => r.IsActive && r.Strategy == cmd.Strategy)
            .OrderByDescending(r => r.Priority)
            .FirstOrDefaultAsync(ct)
            ?? DispatchingRule.Default();

        // 5. Exclude permanently excluded agents
        var eligible = rules.ExcludedAgentIds.Count > 0
            ? candidates.Where(a => !rules.ExcludedAgentIds.Contains(a.Id)).ToList()
            : candidates;

        if (eligible.Count == 0)
            return Result.Fail<DispatchTaskResult>("NO_AGENT_AVAILABLE_AFTER_EXCLUSIONS");

        // 5a. Saturation filter (US-M13-082): exclude agents at task capacity.
        var unsaturated = new List<AgentSummary>(eligible.Count);
        foreach (var agent in eligible)
        {
            var openTasks = await capacityService.GetOpenTaskCountAsync(cmd.TenantId, agent.Id, ct);
            if (openTasks < rules.MaxTasksPerAgent)
                unsaturated.Add(agent);
        }

        if (unsaturated.Count == 0)
        {
            logger.LogWarning(
                "Task {TaskId} blocked: all eligible agents are at task capacity ({Max})",
                task.Id, rules.MaxTasksPerAgent);
            return Result.Fail<DispatchTaskResult>("ALL_AGENTS_AT_TASK_CAPACITY");
        }

        // 6. Score candidates using the same engine as lead dispatch (US-M13-072)
        var strategy = strategyFactory.Create(cmd.Strategy);
        var scored   = await strategy.EvaluateAsync(lead, unsaturated, rules, scorer, ct);

        // 7. Apply the anti-monopoly filter (same threshold as lead dispatch)
        var ranked = scored
            .Where(s => s.Agent.HotLeadsCount < rules.AntiMonopolyThreshold)
            .OrderByDescending(s => s.CompatibilityScore)
            .ToList();

        if (ranked.Count == 0)
        {
            logger.LogWarning(
                "Task {TaskId} blocked by anti-monopoly threshold ({Threshold})",
                task.Id, rules.AntiMonopolyThreshold);
            return Result.Fail<DispatchTaskResult>("ANTI_MONOPOLY_BLOCKED");
        }

        var winner = ranked.First();

        // 8. Persist scored dispatch — stores compatibility audit on the task
        var dispatchResult = task.Dispatch(winner.Agent.Id, winner.CompatibilityScore, winner.FactorsJson);
        if (dispatchResult.IsFailure)
            return Result.Fail<DispatchTaskResult>(dispatchResult.Error!);

        await db.SaveChangesAsync(ct);

        // Invalidate capacity for the winning agent — they now hold one more open task.
        await capacityService.InvalidateAsync(cmd.TenantId, winner.Agent.Id, ct);

        logger.LogInformation(
            "Task {TaskId} dispatched to agent {AgentId} (score={Score}, strategy={Strategy})",
            task.Id, winner.Agent.Id, winner.CompatibilityScore, cmd.Strategy);

        return Result.Ok(new DispatchTaskResult(
            AgentId:                  winner.Agent.Id,
            AgentName:                winner.Agent.FullName,
            CompatibilityScore:       winner.CompatibilityScore,
            CompatibilityFactorsJson: winner.FactorsJson));
    }
}
