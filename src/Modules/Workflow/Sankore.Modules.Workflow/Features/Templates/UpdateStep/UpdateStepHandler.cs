using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.UpdateStep;

internal sealed class UpdateStepHandler(WorkflowDbContext db)
    : IRequestHandler<UpdateStepCommand, Result>
{
    public async Task<Result> Handle(UpdateStepCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail("Template not found.");

        if (template.IsActive)
            return Result.Fail("Cannot modify steps of an active template. Deactivate it first.");

        var step = template.Steps.FirstOrDefault(s => s.Id == request.StepId);
        if (step is null)
            return Result.Fail("Step not found.");

        try
        {
            step.Update(request.Name, request.Description, request.ApproverRoleCode, request.TimeoutHours);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
