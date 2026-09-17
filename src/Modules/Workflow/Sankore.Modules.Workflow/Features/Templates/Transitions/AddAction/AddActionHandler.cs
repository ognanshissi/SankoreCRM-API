using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddAction;

internal sealed class AddActionHandler(WorkflowDbContext db)
    : IRequestHandler<AddActionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddActionCommand request, CancellationToken ct)
    {
        var transitionExists = await db.WorkflowTransitions
            .AnyAsync(t => t.Id == request.TransitionId && t.TemplateId == request.TemplateId, ct);

        if (!transitionExists)
            return Result.Fail<Guid>($"Transition {request.TransitionId} not found in template {request.TemplateId}.");

        var action = WorkflowAction.Create(
            request.TemplateId,
            request.TransitionId,
            request.ActionType,
            request.ConfigJson,
            request.ExecutionOrder);

        db.WorkflowActions.Add(action);
        await db.SaveChangesAsync(ct);
        return Result.Ok(action.Id);
    }
}
