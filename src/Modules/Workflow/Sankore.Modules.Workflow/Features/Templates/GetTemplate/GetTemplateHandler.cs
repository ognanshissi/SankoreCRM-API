using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.GetTemplate;

internal sealed class GetTemplateHandler(WorkflowDbContext db)
    : IRequestHandler<GetTemplateQuery, Result<WorkflowTemplateDto>>
{
    public async Task<Result<WorkflowTemplateDto>> Handle(
        GetTemplateQuery request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        return template is null
            ? Result.Fail<WorkflowTemplateDto>($"Template {request.TemplateId} not found.")
            : Result.Ok(template.ToDto());
    }
}
