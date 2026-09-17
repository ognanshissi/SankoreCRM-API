using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.CancelTask;

internal sealed class CancelTaskHandler(WorkflowDbContext db)
    : IRequestHandler<CancelTaskCommand, Result>
{
    public async Task<Result> Handle(CancelTaskCommand request, CancellationToken ct)
    {
        var task = await db.WorkflowTasks
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TaskId && t.InstanceId == request.InstanceId, ct);

        if (task is null)
            return Result.Fail($"Task {request.TaskId} not found.");

        try
        {
            task.Cancel();
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
