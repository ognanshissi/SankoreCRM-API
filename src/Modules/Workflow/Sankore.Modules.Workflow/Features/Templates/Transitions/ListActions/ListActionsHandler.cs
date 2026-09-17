using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.ListActions;

internal sealed class ListActionsHandler(WorkflowDbContext db)
    : IRequestHandler<ListActionsQuery, Result<List<ActionDto>>>
{
    public async Task<Result<List<ActionDto>>> Handle(ListActionsQuery request, CancellationToken ct)
    {
        var transitionExists = await db.WorkflowTransitions
            .AnyAsync(t => t.Id == request.TransitionId && t.TemplateId == request.TemplateId, ct);

        if (!transitionExists)
            return Result.Fail<List<ActionDto>>(
                $"Transition {request.TransitionId} not found in template {request.TemplateId}.");

        var actions = await db.WorkflowActions
            .Where(a => a.TransitionId == request.TransitionId)
            .OrderBy(a => a.ExecutionOrder)
            .Select(a => new ActionDto(a.Id, a.ActionType, a.ExecutionOrder, a.ConfigJson))
            .ToListAsync(ct);

        return Result.Ok(actions);
    }
}
