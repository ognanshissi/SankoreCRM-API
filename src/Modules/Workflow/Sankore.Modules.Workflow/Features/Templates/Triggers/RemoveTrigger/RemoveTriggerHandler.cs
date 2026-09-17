using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.RemoveTrigger;

internal sealed class RemoveTriggerHandler(WorkflowDbContext db)
    : IRequestHandler<RemoveTriggerCommand, Result>
{
    public async Task<Result> Handle(RemoveTriggerCommand request, CancellationToken ct)
    {
        var trigger = await db.WorkflowTriggers
            .FirstOrDefaultAsync(t => t.Id         == request.TriggerId
                                   && t.TemplateId == request.TemplateId, ct);

        if (trigger is null)
            return Result.Fail("Trigger not found.");

        db.WorkflowTriggers.Remove(trigger);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
