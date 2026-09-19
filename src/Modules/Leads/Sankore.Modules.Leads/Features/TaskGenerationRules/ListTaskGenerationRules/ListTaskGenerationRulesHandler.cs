namespace Sankore.Modules.Leads.Features.TaskGenerationRules.ListTaskGenerationRules;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListTaskGenerationRulesHandler(LeadsDbContext db)
    : IRequestHandler<ListTaskGenerationRulesQuery, Result<IReadOnlyList<TaskGenerationRuleDto>>>
{
    public async Task<Result<IReadOnlyList<TaskGenerationRuleDto>>> Handle(
        ListTaskGenerationRulesQuery query, CancellationToken ct)
    {
        var q = db.TaskGenerationRules.AsQueryable();

        if (query.ActiveOnly == true) q = q.Where(r => r.IsActive);

        var rules = await q
            .OrderBy(r => r.TriggerEventType)
            .Select(r => new TaskGenerationRuleDto(
                r.Id, r.TenantId, r.TriggerEventType, r.TaskType, r.Priority,
                r.TitleTemplate, r.DescriptionTemplate, r.SlaDuration, r.DueDuration,
                r.IsActive, r.CreatedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<TaskGenerationRuleDto>>(rules);
    }
}
