using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.RemoveRule;

internal sealed class RemoveRuleHandler(WorkflowDbContext db)
    : IRequestHandler<RemoveRuleCommand, Result>
{
    public async Task<Result> Handle(RemoveRuleCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .ThenInclude(s => s.Rules)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail($"Template {request.TemplateId} not found.");

        if (template.IsActive)
            return Result.Fail("Cannot modify rules on an active template. Deactivate it first.");

        var step = template.Steps.FirstOrDefault(s => s.Id == request.StepId);
        if (step is null)
            return Result.Fail($"Step {request.StepId} not found in template.");

        try
        {
            step.RemoveRule(request.RuleId);
            await db.SaveChangesAsync(ct);
            return Result.Ok();
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }
    }
}
