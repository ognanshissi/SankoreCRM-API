using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;

internal sealed class ActivateTemplateHandler(WorkflowDbContext db)
    : IRequestHandler<ActivateTemplateCommand, Result>
{
    public async Task<Result> Handle(ActivateTemplateCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail($"Template {request.TemplateId} not found.");

        if (template.IsActive)
            return Result.Fail("Template is already active.");

        // Check no other template for same entityType is already active
        var conflictExists = await db.WorkflowTemplates
            .AnyAsync(t => t.EntityType == template.EntityType
                        && t.IsActive
                        && t.Id != template.Id, ct);

        if (conflictExists)
            return Result.Fail(
                $"Another active template for entity type '{template.EntityType}' already exists. " +
                "Deactivate it first.");

        try
        {
            template.Activate();
        }
        catch (Sankore.Shared.Kernel.DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
