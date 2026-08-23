using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ApproveStep;

internal sealed class ApproveStepHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser,
    IBus bus) : IRequestHandler<ApproveStepCommand, Result>
{
    public async Task<Result> Handle(ApproveStepCommand request, CancellationToken ct)
    {
        var instance = await db.WorkflowInstances
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, ct);

        if (instance is null)
            return Result.Fail($"Instance {request.InstanceId} not found.");

        if (instance.Status is not WorkflowStatus.InProgress)
            return Result.Fail($"Instance is not in progress (current status: {instance.Status}).");

        // Validate role: if the current step requires a specific role, enforce it.
        var currentStep = instance.Steps
            .FirstOrDefault(s => s.Order == instance.CurrentStepOrder
                              && s.Status == StepStatus.AwaitingApproval);

        if (currentStep?.ApproverRoleCode is { } requiredRole &&
            !currentUser.Roles.Contains(requiredRole, StringComparer.OrdinalIgnoreCase))
        {
            return Result.Fail(
                $"Step {instance.CurrentStepOrder} requires role '{requiredRole}'. " +
                "You do not have the required role.");
        }

        try
        {
            instance.Approve(currentUser.Id, request.Comment);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);

        // Publish integration event if workflow completed.
        if (instance.Status == WorkflowStatus.Completed)
        {
            await bus.Publish(new WorkflowCompletedIntegrationEvent(
                EventId: Guid.NewGuid(),
                InstanceId: instance.Id,
                TenantId: instance.TenantId,
                EntityType: instance.EntityType,
                EntityId: instance.EntityId,
                OccurredAt: DateTimeOffset.UtcNow), ct);
        }

        return Result.Ok();
    }
}
