using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.ListTriggers;

internal sealed class ListTriggersHandler(WorkflowDbContext db)
    : IRequestHandler<ListTriggersQuery, Result<IReadOnlyList<TriggerDto>>>
{
    public async Task<Result<IReadOnlyList<TriggerDto>>> Handle(
        ListTriggersQuery request, CancellationToken ct)
    {
        var triggers = await db.WorkflowTriggers
            .Where(t => t.TemplateId == request.TemplateId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new TriggerDto(
                t.Id,
                t.TriggerType,
                t.EventName,
                t.ConditionJson,
                t.IsActive,
                t.CreatedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<TriggerDto>>(triggers);
    }
}
