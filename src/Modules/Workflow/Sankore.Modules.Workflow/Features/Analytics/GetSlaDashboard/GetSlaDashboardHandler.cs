using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetSlaDashboard;

internal sealed class GetSlaDashboardHandler(WorkflowDbContext db)
    : IRequestHandler<GetSlaDashboardQuery, Result<SlaDashboardDto>>
{
    public async Task<Result<SlaDashboardDto>> Handle(
        GetSlaDashboardQuery request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // ── All steps completed (timed-out = breach) or still pending within the window ──
        var windowSteps = await db.WorkflowInstanceSteps
            .Where(s => s.CreatedAt >= request.From && s.CreatedAt <= request.To
                     && s.Status != StepStatus.Pending)
            .Select(s => new
            {
                s.InstanceId,
                s.StepDefinitionId,
                s.Name,
                s.Status,
                s.DueAt,
                s.CreatedAt,
                s.CompletedAt
            })
            .ToListAsync(ct);

        // Map instance → template for breach-rate-per-template grouping.
        var instanceIds = windowSteps.Select(s => s.InstanceId).Distinct().ToList();

        var instanceTemplateMap = await db.WorkflowInstances
            .Where(i => instanceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.TemplateId, i.EntityType })
            .ToListAsync(ct);

        var templateNames = await db.WorkflowTemplates
            .Where(t => instanceTemplateMap.Select(m => m.TemplateId).Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var instanceMap = instanceTemplateMap.ToDictionary(m => m.Id);

        // ── Breach rate per template ──
        var byTemplate = windowSteps
            .GroupBy(s => instanceMap.TryGetValue(s.InstanceId, out var m) ? m.TemplateId : Guid.Empty)
            .Where(g => g.Key != Guid.Empty)
            .Select(g =>
            {
                var total       = g.Count();
                var breachCount = g.Count(s => s.Status == StepStatus.TimedOut);
                var templateId  = g.Key;

                return new SlaTemplateBreachDto(
                    TemplateId:   templateId,
                    TemplateName: templateNames.TryGetValue(templateId, out var n) ? n : "Unknown",
                    TotalSteps:   total,
                    BreachedSteps: breachCount,
                    BreachRate:   total > 0 ? Math.Round((double)breachCount / total, 4) : 0);
            })
            .OrderByDescending(t => t.BreachRate)
            .ToList();

        // ── Daily breach trend ──
        var dailyTrend = windowSteps
            .Where(s => s.Status == StepStatus.TimedOut && s.CompletedAt.HasValue)
            .GroupBy(s => s.CompletedAt!.Value.Date)
            .Select(g => new DailyBreachDto(
                Date:         g.Key,
                BreachCount:  g.Count()))
            .OrderBy(d => d.Date)
            .ToList();

        // ── Currently overdue steps (active steps past their DueAt) ──
        var overdueSteps = await db.WorkflowInstanceSteps
            .Where(s => s.Status == StepStatus.AwaitingApproval
                     && s.DueAt.HasValue
                     && s.DueAt.Value < now)
            .OrderBy(s => s.DueAt)
            .Select(s => new
            {
                s.Id,
                s.InstanceId,
                s.StepDefinitionId,
                s.Name,
                s.DueAt,
                s.AssignedToUserId,
                s.ApproverRoleCode
            })
            .ToListAsync(ct);

        // Load instance info for overdue steps.
        var overdueInstanceIds = overdueSteps.Select(s => s.InstanceId).Distinct().ToList();
        var overdueInstanceInfo = await db.WorkflowInstances
            .Where(i => overdueInstanceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.TemplateId, i.EntityType, i.EntityId })
            .ToDictionaryAsync(i => i.Id, ct);

        var overdueList = overdueSteps.Select(s =>
        {
            overdueInstanceInfo.TryGetValue(s.InstanceId, out var inst);
            return new OverdueStepDto(
                StepId:          s.Id,
                InstanceId:      s.InstanceId,
                EntityType:      inst?.EntityType ?? "Unknown",
                EntityId:        inst?.EntityId ?? Guid.Empty,
                StepName:        s.Name,
                DueAt:           s.DueAt!.Value,
                OverdueByHours:  Math.Round((now - s.DueAt!.Value).TotalHours, 1),
                AssignedToUserId: s.AssignedToUserId,
                ApproverRoleCode: s.ApproverRoleCode);
        }).ToList();

        return Result.Ok(new SlaDashboardDto(
            From:          request.From,
            To:            request.To,
            ByTemplate:    byTemplate,
            DailyTrend:    dailyTrend,
            OverdueSteps:  overdueList));
    }
}
