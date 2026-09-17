using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.AssignStep;

internal sealed class AssignStepHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<AssignStepCommand, Result>
{
    public async Task<Result> Handle(AssignStepCommand request, CancellationToken ct)
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
            step.Assign(request.AssignedToUserId);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        // Audit: record the assignment action.
        var audit = WorkflowAuditEntry.Create(
            tenantId:      instance.TenantId,
            instanceId:    instance.Id,
            fromStateId:   step.StepDefinitionId,
            toStateId:     step.StepDefinitionId,
            eventCode:     EventCodes.Assign,
            actedByUserId: currentUser.Id,
            comment:       $"Assigned to user {request.AssignedToUserId}",
            contextSnapshot: instance.ContextJson);

        db.WorkflowAuditEntries.Add(audit);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
