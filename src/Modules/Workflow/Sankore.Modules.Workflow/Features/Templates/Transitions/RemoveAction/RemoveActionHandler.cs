using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.RemoveAction;

internal sealed class RemoveActionHandler(WorkflowDbContext db)
    : IRequestHandler<RemoveActionCommand, Result>
{
    public async Task<Result> Handle(RemoveActionCommand request, CancellationToken ct)
    {
        var action = await db.WorkflowActions
            .FirstOrDefaultAsync(a =>
                a.Id == request.ActionId &&
                a.TransitionId == request.TransitionId &&
                a.TemplateId == request.TemplateId, ct);

        if (action is null)
            return Result.Fail($"Action {request.ActionId} not found.");

        db.WorkflowActions.Remove(action);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
