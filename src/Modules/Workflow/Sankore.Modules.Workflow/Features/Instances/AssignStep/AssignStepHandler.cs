using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.AssignStep;

/// <summary>
/// Hands an open step to someone. Since « Santé des workflows » (2026-11-03) the history names the
/// person instead of an identifier, and the new holder hears about it — a step handed over in silence
/// waited for someone who did not know.
/// </summary>
internal sealed class AssignStepHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser,
    IAdministrationModule? administration = null,
    INotificationsModule? notifications = null
) : IRequestHandler<AssignStepCommand, Result>
{
    public const string Category = "workflow.assignment";

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

        var reason = request.Reason?.Trim();
        if (reason?.Length > 500)
            return Result.Fail("Le motif ne peut pas dépasser 500 caractères.");

        var names = administration is null
            ? new Dictionary<Guid, string>()
            : await administration.GetUserNamesAsync(instance.TenantId, [request.AssignedToUserId], ct);
        if (administration is not null && !names.ContainsKey(request.AssignedToUserId))
            return Result.Fail("Cette personne n'existe pas dans l'institution.");
        var name = names.GetValueOrDefault(request.AssignedToUserId) ?? request.AssignedToUserId.ToString();

        try
        {
            step.Assign(request.AssignedToUserId);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:      instance.TenantId,
            instanceId:    instance.Id,
            fromStateId:   step.StepDefinitionId,
            toStateId:     step.StepDefinitionId,
            eventCode:     EventCodes.Assign,
            actedByUserId: currentUser.Id,
            comment:       string.IsNullOrEmpty(reason) ? $"Étape confiée à {name}." : $"Étape confiée à {name} : {reason}",
            contextSnapshot: instance.ContextJson));
        await db.SaveChangesAsync(ct);

        if (notifications is not null && request.AssignedToUserId != currentUser.Id)
            await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                instance.TenantId, request.AssignedToUserId, Category,
                $"Étape à décider : « {step.Name} »",
                string.IsNullOrEmpty(reason) ? "Cette étape vous a été confiée." : $"Cette étape vous a été confiée : {reason}",
                $"/settings/workflows/instances/{instance.Id}",
                $"workflow.assignment:{step.Id}:{request.AssignedToUserId}:{DateTimeOffset.UtcNow:O}"), ct);

        return Result.Ok();
    }
}
