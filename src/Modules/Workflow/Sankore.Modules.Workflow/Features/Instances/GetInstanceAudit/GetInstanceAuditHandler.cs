using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetInstanceAudit;

internal sealed class GetInstanceAuditHandler(WorkflowDbContext db)
    : IRequestHandler<GetInstanceAuditQuery, Result<List<WorkflowAuditEntryDto>>>
{
    public async Task<Result<List<WorkflowAuditEntryDto>>> Handle(
        GetInstanceAuditQuery request, CancellationToken ct)
    {
        var instanceExists = await db.WorkflowInstances
            .AnyAsync(i => i.Id == request.InstanceId, ct);

        if (!instanceExists)
            return Result.Fail<List<WorkflowAuditEntryDto>>($"Instance {request.InstanceId} not found.");

        var entries = await db.WorkflowAuditEntries
            .Where(a => a.InstanceId == request.InstanceId)
            .OrderBy(a => a.OccurredAt)
            .Select(a => new WorkflowAuditEntryDto(
                a.Id,
                a.FromStateId,
                a.ToStateId,
                a.EventCode,
                a.ActedByUserId,
                a.Comment,
                a.OccurredAt))
            .ToListAsync(ct);

        return Result.Ok(entries);
    }
}
