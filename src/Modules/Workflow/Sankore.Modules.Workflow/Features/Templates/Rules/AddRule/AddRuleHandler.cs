using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.AddRule;

internal sealed class AddRuleHandler(WorkflowDbContext db)
    : IRequestHandler<AddRuleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddRuleCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .ThenInclude(s => s.Rules)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail<Guid>($"Template {request.TemplateId} not found.");

        if (template.IsActive)
            return Result.Fail<Guid>("Cannot modify rules on an active template. Deactivate it first.");

        var step = template.Steps.FirstOrDefault(s => s.Id == request.StepId);
        if (step is null)
            return Result.Fail<Guid>($"Step {request.StepId} not found in template.");

        try
        {
            var rule = step.AddRule(request.RuleType, request.Field, request.Operator,
                                    request.Value, request.LogicalGroup);
            db.WorkflowRules.Add(rule);
            await db.SaveChangesAsync(ct);
            return Result.Ok(rule.Id);
        }
        catch (DomainException ex)
        {
            return Result.Fail<Guid>(ex.Message);
        }
    }
}
