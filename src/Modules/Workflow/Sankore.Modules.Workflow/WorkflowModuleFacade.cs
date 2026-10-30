using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow;

/// <summary>
/// Internal implementation of <see cref="IWorkflowModule"/>.
/// Exposed to the DI container as the concrete type behind the public contract.
/// </summary>
internal sealed class WorkflowModuleFacade(WorkflowDbContext db) : IWorkflowModule
{
    public async Task<Result<Guid>> StartWorkflowAsync(
        WorkflowStartRequest request,
        CancellationToken ct = default)
    {
        var template = await db.WorkflowTemplates
            .IgnoreQueryFilters()
            .Include(t => t.Steps)
            .Where(t => t.TenantId == request.TenantId
                     && t.EntityType == request.EntityType
                     && t.IsActive
                     && (request.Circuit == null || t.Circuit == request.Circuit))
            .OrderBy(t => t.Circuit)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            return Result.Fail<Guid>(request.Circuit is null
                ? $"No active workflow template found for entity type '{request.EntityType}'."
                : $"No active '{request.EntityType}' workflow for circuit '{request.Circuit}'.");

        // A caller-supplied ladder that matches no step would skip EVERY step, and an instance with
        // nothing left to do completes itself on the spot — reporting a fully approved entity
        // nobody ever signed for. Refusing is the only safe answer: the caller's ladder and this
        // template disagree, which is a configuration fault, not an approval.
        if (request.RequiredStepOrders is { } required)
        {
            var known = template.Steps.Select(s => s.Order).ToHashSet();
            if (!required.Any(known.Contains))
            {
                return Result.Fail<Guid>(
                    $"None of the requested step orders [{string.Join(", ", required)}] exist in the "
                    + $"active '{request.EntityType}' template, whose steps are "
                    + $"[{string.Join(", ", known.Order())}].");
            }
        }

        var instance = WorkflowInstance.Start(
            template, request.EntityId, request.StartedByUserId,
            requiredStepOrders: request.RequiredStepOrders);

        if (request.Subject is { } subject)
            instance.DescribeSubject(subject.Label, subject.Detail, subject.Link);

        db.WorkflowInstances.Add(instance);

        if (instance.PendingAuditEntries.Count > 0)
            db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);

        await db.SaveChangesAsync(ct);

        return Result.Ok(instance.Id);
    }

    public async Task<IReadOnlyList<WorkflowInstanceDecisions>> GetInstanceDecisionsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> instanceIds, CancellationToken ct = default)
    {
        if (instanceIds.Count == 0)
            return [];

        var instances = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .Include(i => i.Steps)
            .Where(i => i.TenantId == tenantId && instanceIds.Contains(i.Id))
            .ToListAsync(ct);

        var templateIds = instances.Select(i => i.TemplateId).Distinct().ToList();
        var names = await db.WorkflowTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        return instances
            .Select(i => new WorkflowInstanceDecisions(
                i.Id,
                names.GetValueOrDefault(i.TemplateId, string.Empty),
                i.Status.ToString(),
                i.StartedAt,
                i.CompletedAt,
                i.Steps
                    .OrderBy(s => s.Order)
                    .Select(s => new WorkflowStepDecision(
                        s.Order, s.Name, s.ApproverRoleCode, s.Status.ToString(), s.ActedByUserId,
                        s.Comment, s.DueAt, s.CompletedAt))
                    .ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowCircuitSummary>> ListActiveCircuitsAsync(
        Guid tenantId, string entityType, CancellationToken ct = default)
        => await db.WorkflowTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.EntityType == entityType && t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new WorkflowCircuitSummary(t.Circuit, t.Name, t.Version, t.Steps.Count))
            .ToListAsync(ct);

    public async Task<Result<WorkflowInstanceStatus>> GetInstanceStatusAsync(
        Guid instanceId,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var instance = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct);

        if (instance is null)
            return Result.Fail<WorkflowInstanceStatus>("Workflow instance not found.");

        return Result.Ok(new WorkflowInstanceStatus(
            InstanceId: instance.Id,
            Status: instance.Status.ToString(),
            CurrentStepOrder: instance.CurrentStepOrder,
            TotalSteps: instance.Steps.Count,
            IsCompleted: instance.Status == WorkflowStatus.Completed));
    }

    /// <summary>
    /// Records a decision the caller has already taken. See <see cref="IWorkflowModule"/> for why
    /// this performs no entitlement check.
    ///
    /// <para>
    /// Deliberately narrower than <c>ApproveStepHandler</c>, the endpoint a human uses:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>It runs no <c>ApproverRoleCode</c> check. The decision was authorised by the calling
    ///   module under its own permission, and a second, role-shaped check here would refuse a
    ///   decision that has already been applied — leaving the instance permanently behind the
    ///   entity it tracks.</item>
    /// <item>It runs no transition ACTIONS and evaluates no transition CONDITIONS — it does not use
    ///   the state machine at all (see the comment at the decision below). That is a consequence of
    ///   skipped steps, and a fair trade here: an action is a side effect a template designer asked
    ///   the engine to perform, and the deciding module never agreed to it. A template whose KYC
    ///   steps carry actions will find they do not fire on a mirrored decision.</item>
    /// <item>It publishes no completion event. A mirrored instance has no parent to resume, and the
    ///   calling module announces its own outcome — two announcements of one decision is exactly
    ///   what module boundaries exist to prevent.</item>
    /// </list>
    /// </summary>
    public async Task<Result> RecordDecisionAsync(
        RecordWorkflowDecisionRequest request,
        CancellationToken ct = default)
    {
        var instance = await db.WorkflowInstances
            .AsTracking()
            .IgnoreQueryFilters()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId && i.TenantId == request.TenantId, ct);

        if (instance is null)
            return Result.Fail($"Workflow instance {request.InstanceId} not found.");

        // Terminal covers the case this contract was written around: a step that blew its SLA leaves
        // the instance TimedOut, and every later decision has nowhere to land. The caller logs and
        // carries on.
        if (instance.Status is WorkflowStatus.Completed or WorkflowStatus.Rejected
                            or WorkflowStatus.Cancelled or WorkflowStatus.TimedOut)
        {
            return Result.Fail(
                $"Workflow instance {instance.Id} is already {instance.Status} and records nothing further.");
        }

        if (request.Decision == WorkflowDecision.Cancelled)
        {
            instance.Cancel();
            await db.SaveChangesAsync(ct);
            return Result.Ok();
        }

        // Approve and Reject act on the step the engine believes is current. A mismatch means the
        // caller's ladder and this instance have drifted apart, which is worth reporting rather than
        // silently applying the decision to whichever step happens to be open.
        if (instance.CurrentStepOrder != request.StepOrder)
        {
            return Result.Fail(
                $"Instance {instance.Id} is awaiting step {instance.CurrentStepOrder}, "
                + $"not step {request.StepOrder}.");
        }

        // Captured before the decision moves the instance: it is the "from" side of the audit entry.
        var fromStateId = instance.CurrentStateId;

        try
        {
            // DELIBERATELY WITHOUT the template's transitions, which puts the aggregate on its
            // rule-based advance instead of the state machine.
            //
            // The state machine cannot be used here. Activate() chains the transitions linearly —
            // step N's APPROVE points at step N+1 — with no knowledge of which steps this entity
            // skipped, so Fire() would call StartReview() on a Skipped step and that throws. The
            // rule-based path walks the steps still Pending, which is exactly the right behaviour
            // for a ladder whose shape the caller chose: it lands on the next rung that applies and
            // completes when none are left.
            //
            // The cost is that Fire() is also what writes the audit entry, so this method writes its
            // own below rather than losing the trail that is the whole point of mirroring.
            if (request.Decision == WorkflowDecision.Approved)
                instance.Approve(request.ActedByUserId, request.Comment);
            else
                instance.Reject(request.ActedByUserId, request.Comment);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId: instance.TenantId,
            instanceId: instance.Id,
            fromStateId: fromStateId,
            // Null once the instance is finished, which is what a terminal entry should read as.
            toStateId: instance.CurrentStateId,
            eventCode: request.Decision == WorkflowDecision.Approved
                ? EventCodes.Approve
                : EventCodes.Reject,
            actedByUserId: request.ActedByUserId,
            comment: request.Comment,
            contextSnapshot: instance.ContextJson));

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    public async Task<IReadOnlyList<string>> GetBlockingStepsAsync(
        Guid tenantId, string entityType, Guid entityId, CancellationToken ct = default)
    {
        // The flag is the definition's (« Étape bloquante » in the builder); the instance step says
        // whether it is passed. Skipped (a branch not taken) and passed steps block nothing.
        var blocking = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId
                     && (s.Status == StepStatus.Pending || s.Status == StepStatus.AwaitingApproval))
            .Join(db.WorkflowInstances.IgnoreQueryFilters()
                    .Where(i => i.TenantId == tenantId && i.EntityType == entityType && i.EntityId == entityId
                             && (i.Status == WorkflowStatus.InProgress || i.Status == WorkflowStatus.Pending
                                 || i.Status == WorkflowStatus.WaitingForChild)),
                s => s.InstanceId, i => i.Id, (s, i) => new { s.StepDefinitionId, s.Name, s.Order, i.TemplateId })
            .Join(db.WorkflowStepDefinitions.IgnoreQueryFilters().Where(d => d.IsBlocking),
                x => x.StepDefinitionId, d => d.Id, (x, d) => x)
            .Join(db.WorkflowTemplates.IgnoreQueryFilters(), x => x.TemplateId, t => t.Id,
                (x, t) => new { x.Name, x.Order, Circuit = t.Name })
            .OrderBy(x => x.Circuit).ThenBy(x => x.Order)
            .ToListAsync(ct);

        return blocking.Select(b => $"{b.Name} ({b.Circuit})").ToList();
    }

    public async Task<IReadOnlyList<StepSlaStat>> GetStepSlaStatsAsync(
        Guid tenantId, Guid userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var rows = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId
                     && s.DueAt != null
                     && s.CompletedAt >= from && s.CompletedAt < to
                     && (((s.Status == StepStatus.Approved || s.Status == StepStatus.Rejected) && s.ActedByUserId == userId)
                         || (s.Status == StepStatus.TimedOut && s.AssignedToUserId == userId)))
            .GroupBy(s => s.Name)
            .Select(g => new
            {
                Name = g.Key,
                Total = g.Count(),
                OnTime = g.Count(s => s.Status != StepStatus.TimedOut && s.CompletedAt <= s.DueAt),
            })
            .ToListAsync(ct);

        return rows.Select(r => new StepSlaStat(r.Name, r.Total, r.OnTime)).ToList();
    }
}
