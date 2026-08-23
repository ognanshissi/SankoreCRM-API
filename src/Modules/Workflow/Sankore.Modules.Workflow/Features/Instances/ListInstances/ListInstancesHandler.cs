using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ListInstances;

internal sealed class ListInstancesHandler(WorkflowDbContext db)
    : IRequestHandler<ListInstancesQuery, Result<List<WorkflowInstanceDto>>>
{
    public async Task<Result<List<WorkflowInstanceDto>>> Handle(
        ListInstancesQuery request, CancellationToken ct)
    {
        var query = db.WorkflowInstances
            .Include(i => i.Steps)
            .AsQueryable();

        if (request.EntityType is not null)
            query = query.Where(i => i.EntityType == request.EntityType);

        if (request.EntityId.HasValue)
            query = query.Where(i => i.EntityId == request.EntityId.Value);

        if (request.Status is not null &&
            Enum.TryParse<WorkflowStatus>(request.Status, ignoreCase: true, out var status))
            query = query.Where(i => i.Status == status);

        var instances = await query
            .OrderByDescending(i => i.StartedAt)
            .ToListAsync(ct);

        return Result.Ok(instances.Select(i => i.ToDto()).ToList());
    }
}
