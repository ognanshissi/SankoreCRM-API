using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetExecutionMonitor;

internal sealed class GetExecutionMonitorHandler(WorkflowDbContext db)
    : IRequestHandler<GetExecutionMonitorQuery, Result<ExecutionMonitorDto>>
{
    public async Task<Result<ExecutionMonitorDto>> Handle(
        GetExecutionMonitorQuery request, CancellationToken ct)
    {
        var now           = DateTimeOffset.UtcNow;
        var stuckCutoff   = now.AddHours(-request.StuckThresholdHours);

        // ── Load all non-terminal instances ──
        var activeInstances = await db.WorkflowInstances
            .Where(i => i.Status == WorkflowStatus.InProgress
                     || i.Status == WorkflowStatus.WaitingForChild)
            .Select(i => new
            {
                i.Id,
                i.TemplateId,
                i.EntityType,
                i.EntityId,
                i.Status,
                i.StartedAt,
                i.CurrentStepOrder
            })
            .ToListAsync(ct);

        var activeInstanceIds = activeInstances.Select(i => i.Id).ToList();

        // Template names for grouping.
        var templateIds = activeInstances.Select(i => i.TemplateId).Distinct().ToList();
        var templateLookup = await db.WorkflowTemplates
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        // ── Per-template active counts ──
        var perTemplate = activeInstances
            .GroupBy(i => i.TemplateId)
            .Select(g => new TemplateQueueDto(
                TemplateId:      g.Key,
                TemplateName:    templateLookup.TryGetValue(g.Key, out var n) ? n : "Unknown",
                ActiveInstances: g.Count(i => i.Status == WorkflowStatus.InProgress),
                WaitingForChild: g.Count(i => i.Status == WorkflowStatus.WaitingForChild)))
            .OrderByDescending(t => t.ActiveInstances)
            .ToList();

        // ── Step queue depths per template ──
        var queueSteps = await db.WorkflowInstanceSteps
            .Where(s => activeInstanceIds.Contains(s.InstanceId)
                     && s.Status == StepStatus.AwaitingApproval)
            .Select(s => new { s.InstanceId, s.StepDefinitionId, s.Name, s.ApproverRoleCode })
            .ToListAsync(ct);

        var instanceTemplateMap = activeInstances.ToDictionary(i => i.Id, i => i.TemplateId);

        var queueByTemplate = queueSteps
            .GroupBy(s => instanceTemplateMap.TryGetValue(s.InstanceId, out var tid) ? tid : Guid.Empty)
            .Where(g => g.Key != Guid.Empty)
            .ToDictionary(g => g.Key, g => g.Count());

        // Merge queue depths into perTemplate list.
        var perTemplateWithQueue = perTemplate
            .Select(t => t with
            {
                QueueDepth = queueByTemplate.TryGetValue(t.TemplateId, out var q) ? q : 0
            })
            .ToList();

        // ── Stuck instances: InProgress longer than threshold with no SLA deadline on current step ──
        var stuckInstances = activeInstances
            .Where(i => i.Status == WorkflowStatus.InProgress
                     && i.StartedAt <= stuckCutoff)
            .ToList();

        // Cross-check: exclude instances whose current step has a DueAt (SLA is handling them).
        var stuckIds = stuckInstances.Select(i => i.Id).ToList();

        var stepsWithDueAt = await db.WorkflowInstanceSteps
            .Where(s => stuckIds.Contains(s.InstanceId)
                     && s.Status == StepStatus.AwaitingApproval
                     && s.DueAt.HasValue)
            .Select(s => s.InstanceId)
            .Distinct()
            .ToListAsync(ct);

        var stuckWithNoSla = stuckInstances
            .Where(i => !stepsWithDueAt.Contains(i.Id))
            .Select(i => new StuckInstanceDto(
                InstanceId:    i.Id,
                EntityType:    i.EntityType,
                EntityId:      i.EntityId,
                TemplateId:    i.TemplateId,
                TemplateName:  templateLookup.TryGetValue(i.TemplateId, out var n) ? n : "Unknown",
                StartedAt:     i.StartedAt,
                StuckForHours: Math.Round((now - i.StartedAt).TotalHours, 1)))
            .OrderByDescending(s => s.StuckForHours)
            .ToList();

        return Result.Ok(new ExecutionMonitorDto(
            AsOf:            now,
            StuckThresholdHours: request.StuckThresholdHours,
            ByTemplate:      perTemplateWithQueue,
            StuckInstances:  stuckWithNoSla));
    }
}
