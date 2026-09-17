using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.DelegateStep;

internal sealed class DelegateStepHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<DelegateStepCommand, Result>
{
    public async Task<Result> Handle(DelegateStepCommand request, CancellationToken ct)
    {
        var instance = await db.WorkflowInstances
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, ct);

        if (instance is null)
            return Result.Fail("Workflow instance not found.");

        var step = instance.Steps.FirstOrDefault(s => s.Id == request.StepId);
        if (step is null)
            return Result.Fail("Step not found.");

        try
        {
            step.Delegate(currentUser.Id, request.ToUserId);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        var audit = WorkflowAuditEntry.Create(
            tenantId:      instance.TenantId,
            instanceId:    instance.Id,
            fromStateId:   step.StepDefinitionId,
            toStateId:     step.StepDefinitionId,
            eventCode:     EventCodes.Delegate,
            actedByUserId: currentUser.Id,
            comment:       request.Comment ?? $"Delegated to user {request.ToUserId}",
            contextSnapshot: instance.ContextJson);

        db.WorkflowAuditEntries.Add(audit);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
