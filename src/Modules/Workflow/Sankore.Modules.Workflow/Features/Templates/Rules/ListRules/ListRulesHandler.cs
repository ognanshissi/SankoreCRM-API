using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.ListRules;

internal sealed class ListRulesHandler(WorkflowDbContext db)
    : IRequestHandler<ListRulesQuery, Result<List<RuleDto>>>
{
    public async Task<Result<List<RuleDto>>> Handle(ListRulesQuery request, CancellationToken ct)
    {
        var templateExists = await db.WorkflowTemplates
            .AnyAsync(t => t.Id == request.TemplateId, ct);

        if (!templateExists)
            return Result.Fail<List<RuleDto>>($"Template {request.TemplateId} not found.");

        var rules = await db.WorkflowRules
            .Where(r => r.TemplateId == request.TemplateId && r.StepId == request.StepId)
            .OrderBy(r => r.LogicalGroup)
            .ThenBy(r => r.RuleType)
            .Select(r => new RuleDto(r.Id, r.RuleType, r.Field, r.Operator, r.Value, r.LogicalGroup))
            .ToListAsync(ct);

        return Result.Ok(rules);
    }
}
