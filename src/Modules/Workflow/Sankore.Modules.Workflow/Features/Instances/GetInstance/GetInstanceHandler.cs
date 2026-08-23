using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetInstance;

internal sealed class GetInstanceHandler(WorkflowDbContext db)
    : IRequestHandler<GetInstanceQuery, Result<WorkflowInstanceDto>>
{
    public async Task<Result<WorkflowInstanceDto>> Handle(
        GetInstanceQuery request, CancellationToken ct)
    {
        var instance = await db.WorkflowInstances
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, ct);

        return instance is null
            ? Result.Fail<WorkflowInstanceDto>($"Instance {request.InstanceId} not found.")
            : Result.Ok(instance.ToDto());
    }
}
