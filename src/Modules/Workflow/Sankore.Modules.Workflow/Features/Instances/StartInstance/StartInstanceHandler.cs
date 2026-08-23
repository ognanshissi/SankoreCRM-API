using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.StartInstance;

internal sealed class StartInstanceHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser) : IRequestHandler<StartInstanceCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(StartInstanceCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.EntityType == request.EntityType && t.IsActive, ct);

        if (template is null)
            return Result.Fail<Guid>(
                $"No active workflow template found for entity type '{request.EntityType}'.");

        if (!template.Steps.Any())
            return Result.Fail<Guid>("The active template has no steps configured.");

        var instance = WorkflowInstance.Start(template, request.EntityId, currentUser.Id);

        db.WorkflowInstances.Add(instance);
        // Explicitly register steps — required because Steps uses a private backing field.
        db.WorkflowInstanceSteps.AddRange(instance.Steps);
        await db.SaveChangesAsync(ct);

        return Result.Ok(instance.Id);
    }
}
