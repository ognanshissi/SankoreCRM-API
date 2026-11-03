using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.CancelInstance;

internal sealed class CancelInstanceHandler(
    WorkflowDbContext db,
    Sankore.Shared.Infrastructure.Auth.ICurrentUser? currentUser = null)
    : IRequestHandler<CancelInstanceCommand, Result>
{
    public async Task<Result> Handle(CancelInstanceCommand request, CancellationToken ct)
    {
        var instance = await db.WorkflowInstances
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, ct);

        if (instance is null)
            return Result.Fail($"Instance {request.InstanceId} not found.");

        var reason = request.Reason?.Trim();
        if (reason?.Length > 500)
            return Result.Fail("Le motif ne peut pas dépasser 500 caractères.");

        try
        {
            instance.Cancel();
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        // A cancellation left no line in the history: who stopped the circuit, and why, was lost.
        var open = instance.Steps.Where(s => s.Status == StepStatus.AwaitingApproval).OrderBy(s => s.Order).FirstOrDefault();
        db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:        instance.TenantId,
            instanceId:      instance.Id,
            fromStateId:     open?.StepDefinitionId,
            toStateId:       null,
            eventCode:       EventCodes.Cancel,
            actedByUserId:   currentUser?.Id ?? Guid.Empty,
            comment:         string.IsNullOrEmpty(reason) ? "Circuit annulé." : $"Circuit annulé : {reason}",
            contextSnapshot: instance.ContextJson));

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
