using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.AddTrigger;

internal sealed class AddTriggerHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<AddTriggerCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddTriggerCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail<Guid>("Workflow template not found.");

        // A partner's event type arrives lower-cased (Leads' external-event endpoint, webhooks lot
        // 6): a trigger typed « Appointment.Booked » would otherwise never match it.
        var eventName = request.TriggerType == TriggerType.ExternalEvent
            ? request.EventName.Trim().ToLowerInvariant()
            : request.EventName;

        // An internal event must be one the platform publishes, about this template's entity: a
        // free name used to be accepted and then never matched anything (« LEAD_QUALIFIED »).
        if (request.TriggerType == TriggerType.EntityEvent && eventName != PlatformEvents.LegacyLeadCaptured)
        {
            var known = PlatformEvents.Find(eventName);
            if (known is null || !known.StartsWorkflows)
                return Result.Fail<Guid>($"Unknown internal event '{eventName}'.");
            if (!string.Equals(known.WorkflowEntityType, template.EntityType, StringComparison.OrdinalIgnoreCase))
                return Result.Fail<Guid>($"Event '{eventName}' is about {known.WorkflowEntityType}, not {template.EntityType}.");
        }

        // Guard duplicate: same template + event name already registered.
        var exists = await db.WorkflowTriggers
            .AnyAsync(t => t.TemplateId == request.TemplateId
                        && t.EventName  == eventName, ct);

        if (exists)
            return Result.Fail<Guid>(
                $"A trigger for event '{eventName}' already exists on this template.");

        var trigger = WorkflowTrigger.Create(
            tenantId:     currentUser.TenantId,
            templateId:   request.TemplateId,
            triggerType:  request.TriggerType,
            eventName:    eventName,
            conditionJson: request.ConditionJson);

        db.WorkflowTriggers.Add(trigger);
        await db.SaveChangesAsync(ct);

        return Result.Ok(trigger.Id);
    }
}
