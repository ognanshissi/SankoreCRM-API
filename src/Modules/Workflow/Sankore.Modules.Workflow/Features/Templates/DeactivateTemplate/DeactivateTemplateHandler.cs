using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.DeactivateTemplate;

internal sealed class DeactivateTemplateHandler(WorkflowDbContext db)
    : IRequestHandler<DeactivateTemplateCommand, Result>
{
    public async Task<Result> Handle(DeactivateTemplateCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail($"Template {request.TemplateId} not found.");

        if (!template.IsActive)
            return Result.Fail("Template is already inactive.");

        template.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
