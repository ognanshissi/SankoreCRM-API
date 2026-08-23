using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.UpdateTemplate;

internal sealed class UpdateTemplateHandler(WorkflowDbContext db)
    : IRequestHandler<UpdateTemplateCommand, Result>
{
    public async Task<Result> Handle(UpdateTemplateCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail($"Template {request.TemplateId} not found.");

        template.Update(request.Name, request.Description);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
