using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetBottlenecks;

internal sealed class GetBottlenecksHandler(WorkflowDbContext db)
    : IRequestHandler<GetBottlenecksQuery, Result<IReadOnlyList<StepBottleneckDto>>>
{
    public async Task<Result<IReadOnlyList<StepBottleneckDto>>> Handle(
        GetBottlenecksQuery request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail<IReadOnlyList<StepBottleneckDto>>("Workflow template not found.");

        var instanceIds = await db.WorkflowInstances
            .Where(i => i.TemplateId == request.TemplateId)
            .Select(i => i.Id)
            .ToListAsync(ct);

        if (instanceIds.Count == 0)
            return Result.Ok<IReadOnlyList<StepBottleneckDto>>([]);

        var steps = await db.WorkflowInstanceSteps
            .Where(s => instanceIds.Contains(s.InstanceId)
                     && s.Status != StepStatus.Pending)   // exclude not-yet-started steps
            .Select(s => new
            {
                s.StepDefinitionId,
                s.Name,
                s.Status,
                s.CreatedAt,
                s.CompletedAt
            })
            .ToListAsync(ct);

        var stepOrderLookup = template.Steps.ToDictionary(s => s.Id, s => s.Order);

        var result = steps
            .GroupBy(s => new { s.StepDefinitionId, s.Name })
            .Select(g =>
            {
                var total    = g.Count();
                var terminal = g.Where(s => s.CompletedAt.HasValue).ToList();

                var approved  = terminal.Count(s => s.Status is StepStatus.Approved or StepStatus.AutoApproved);
                var rejected  = terminal.Count(s => s.Status == StepStatus.Rejected);
                var timedOut  = terminal.Count(s => s.Status == StepStatus.TimedOut);
                var skipped   = terminal.Count(s => s.Status == StepStatus.Skipped);

                var actionable = approved + rejected + timedOut; // steps that required attention

                double avgDuration = terminal.Count > 0
                    ? terminal.Average(s => (s.CompletedAt!.Value - s.CreatedAt).TotalHours)
                    : 0;

                double rejectionRate = actionable > 0 ? (double)rejected  / actionable : 0;
                double timeoutRate   = actionable > 0 ? (double)timedOut  / actionable : 0;

                // Composite bottleneck score — normalise avgDuration to hours capped at 168 (1 week).
                double normDuration = Math.Min(avgDuration, 168) / 168;
                double score = Math.Round(normDuration * 0.5 + rejectionRate * 0.3 + timeoutRate * 0.2, 4);

                return new StepBottleneckDto(
                    StepDefinitionId: g.Key.StepDefinitionId,
                    StepName:         g.Key.Name,
                    Order:            stepOrderLookup.TryGetValue(g.Key.StepDefinitionId, out var o) ? o : 0,
                    TotalExecutions:  total,
                    ApprovedCount:    approved,
                    RejectedCount:    rejected,
                    TimedOutCount:    timedOut,
                    SkippedCount:     skipped,
                    AvgDurationHours: Math.Round(avgDuration, 2),
                    RejectionRate:    Math.Round(rejectionRate, 4),
                    TimeoutRate:      Math.Round(timeoutRate,   4),
                    BottleneckScore:  score);
            })
            .OrderByDescending(s => s.BottleneckScore)
            .ThenByDescending(s => s.AvgDurationHours)
            .ToList();

        return Result.Ok<IReadOnlyList<StepBottleneckDto>>(result);
    }
}
