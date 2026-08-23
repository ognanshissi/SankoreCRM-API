using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.ListTemplates;

internal sealed class ListTemplatesHandler(WorkflowDbContext db)
    : IRequestHandler<ListTemplatesQuery, Result<List<WorkflowTemplateDto>>>
{
    public async Task<Result<List<WorkflowTemplateDto>>> Handle(
        ListTemplatesQuery request, CancellationToken ct)
    {
        var query = db.WorkflowTemplates
            .Include(t => t.Steps)
            .AsQueryable();

        if (request.ActiveOnly.HasValue)
            query = query.Where(t => t.IsActive == request.ActiveOnly.Value);

        var templates = await query
            .OrderBy(t => t.EntityType)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

        return Result.Ok(templates.Select(t => t.ToDto()).ToList());
    }
}
