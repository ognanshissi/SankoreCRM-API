using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.RemoveStep;

internal sealed class RemoveStepHandler(WorkflowDbContext db)
    : IRequestHandler<RemoveStepCommand, Result>
{
    public async Task<Result> Handle(RemoveStepCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail($"Template {request.TemplateId} not found.");

        if (template.IsActive)
            return Result.Fail("Cannot modify steps of an active template. Deactivate it first.");

        try
        {
            template.RemoveStep(request.StepId);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
