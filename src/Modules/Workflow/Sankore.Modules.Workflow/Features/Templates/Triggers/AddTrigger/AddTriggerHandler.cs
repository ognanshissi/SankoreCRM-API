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

        // Guard duplicate: same template + event name already registered.
        var exists = await db.WorkflowTriggers
            .AnyAsync(t => t.TemplateId == request.TemplateId
                        && t.EventName  == request.EventName, ct);

        if (exists)
            return Result.Fail<Guid>(
                $"A trigger for event '{request.EventName}' already exists on this template.");

        var trigger = WorkflowTrigger.Create(
            tenantId:     currentUser.TenantId,
            templateId:   request.TemplateId,
            triggerType:  request.TriggerType,
            eventName:    request.EventName,
            conditionJson: request.ConditionJson);

        db.WorkflowTriggers.Add(trigger);
        await db.SaveChangesAsync(ct);

        return Result.Ok(trigger.Id);
    }
}
