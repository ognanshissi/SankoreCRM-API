using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.AddStep;

internal sealed class AddStepHandler(WorkflowDbContext db)
    : IRequestHandler<AddStepCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddStepCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail<Guid>($"Template {request.TemplateId} not found.");

        if (template.IsActive)
            return Result.Fail<Guid>("Cannot modify steps of an active template. Deactivate it first.");

        try
        {
            var step = template.AddStep(
                request.Order,
                request.Name,
                request.Description,
                request.ApproverRoleCode,
                request.TimeoutHours);

            // Explicitly register the new step entity so EF change tracker picks it
            // up regardless of backing-field access mode (required for InMemory tests).
            db.WorkflowStepDefinitions.Add(step);
            await db.SaveChangesAsync(ct);
            return Result.Ok(step.Id);
        }
        catch (DomainException ex)
        {
            return Result.Fail<Guid>(ex.Message);
        }
    }
}
