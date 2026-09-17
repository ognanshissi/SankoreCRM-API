using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetTemplateStats;

internal sealed class GetTemplateStatsHandler(WorkflowDbContext db)
    : IRequestHandler<GetTemplateStatsQuery, Result<TemplateStatsDto>>
{
    public async Task<Result<TemplateStatsDto>> Handle(
        GetTemplateStatsQuery request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail<TemplateStatsDto>("Workflow template not found.");

        // Instance counts grouped by status.
        var instances = await db.WorkflowInstances
            .Where(i => i.TemplateId == request.TemplateId)
            .Select(i => new { i.Id, i.Status, i.StartedAt, i.CompletedAt })
            .ToListAsync(ct);

        var byStatus = instances
            .GroupBy(i => i.Status.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        // Avg completion time for finished instances (hours).
        var completedInstances = instances
            .Where(i => i.CompletedAt.HasValue)
            .ToList();

        double? avgCompletionHours = completedInstances.Count > 0
            ? completedInstances
                .Average(i => (i.CompletedAt!.Value - i.StartedAt).TotalHours)
            : null;

        // SLA breach count: steps that timed out.
        var instanceIds = instances.Select(i => i.Id).ToList();

        var slaBreachCount = await db.WorkflowInstanceSteps
            .Where(s => instanceIds.Contains(s.InstanceId)
                     && s.Status == StepStatus.TimedOut)
            .CountAsync(ct);

        // Per-step stats derived from WorkflowInstanceStep records.
        var allSteps = await db.WorkflowInstanceSteps
            .Where(s => instanceIds.Contains(s.InstanceId))
            .Select(s => new
            {
                s.StepDefinitionId,
                s.Name,
                s.Status,
                s.CreatedAt,
                s.CompletedAt
            })
            .ToListAsync(ct);

        var stepStats = allSteps
            .GroupBy(s => new { s.StepDefinitionId, s.Name })
            .Select(g =>
            {
                var completed = g.Where(s => s.Status == StepStatus.Approved
                                          || s.Status == StepStatus.AutoApproved).ToList();
                double? avgDuration = completed.Count > 0 && completed.All(s => s.CompletedAt.HasValue)
                    ? completed.Average(s => (s.CompletedAt!.Value - s.CreatedAt).TotalHours)
                    : null;

                return new StepStatsDto(
                    StepDefinitionId: g.Key.StepDefinitionId,
                    StepName:         g.Key.Name,
                    AvgDurationHours: avgDuration.HasValue ? Math.Round(avgDuration.Value, 2) : null,
                    CompletedCount:   g.Count(s => s.Status == StepStatus.Approved || s.Status == StepStatus.AutoApproved),
                    SkippedCount:     g.Count(s => s.Status == StepStatus.Skipped),
                    TimedOutCount:    g.Count(s => s.Status == StepStatus.TimedOut),
                    RejectedCount:    g.Count(s => s.Status == StepStatus.Rejected));
            })
            .OrderBy(s => template.Steps.FirstOrDefault(d => d.Id == s.StepDefinitionId)?.Order ?? 0)
            .ToList();

        return Result.Ok(new TemplateStatsDto(
            TemplateId:         template.Id,
            EntityType:         template.EntityType,
            TemplateName:       template.Name,
            Version:            template.Version,
            TotalInstances:     instances.Count,
            ByStatus:           byStatus,
            AvgCompletionHours: avgCompletionHours.HasValue ? Math.Round(avgCompletionHours.Value, 2) : null,
            SlaBreachCount:     slaBreachCount,
            StepStats:          stepStats));
    }
}
