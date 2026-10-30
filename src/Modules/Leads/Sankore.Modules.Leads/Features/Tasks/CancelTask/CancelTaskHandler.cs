using Sankore.Shared.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Workflow.PublicApi;
namespace Sankore.Modules.Leads.Features.Tasks.CancelTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CancelTaskHandler(
    LeadsDbContext db,
    AgentCapacityService capacityService,
    TaskAccess access,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<CancelTaskCommand, Result>
{
    public async Task<Result> Handle(CancelTaskCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: true, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);
        var task = loaded.Value!;

        var agentId  = task.AssignedAgentId;
        var tenantId = task.TenantId;

        var result = task.Cancel();
        if (result.IsFailure) return result;

        // The task of a workflow « Tâche manuelle » step: the step stays open, approvable by hand.
        if (task.TriggerEventType == WorkflowTaskRequested.TriggerType && task.TriggerEventId is { } stepId)
            await publisher.PublishAsync(new WorkflowStepTaskClosed(task.TenantId, stepId, false, null, task.Title), ct);

        await db.SaveChangesAsync(ct);

        if (agentId.HasValue)
            await capacityService.InvalidateAsync(tenantId, agentId.Value, ct);

        return Result.Ok();
    }
}
