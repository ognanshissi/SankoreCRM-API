using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddTransition;

internal sealed class AddTransitionHandler(WorkflowDbContext db)
    : IRequestHandler<AddTransitionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddTransitionCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail<Guid>($"Template {request.TemplateId} not found.");

        try
        {
            var transition = template.AddTransition(
                request.FromStateId,
                request.ToStateId,
                request.EventCode,
                request.ToTerminalStatus,
                request.ConditionJson,
                request.Priority);

            db.WorkflowTransitions.Add(transition);
            await db.SaveChangesAsync(ct);
            return Result.Ok(transition.Id);
        }
        catch (DomainException ex)
        {
            return Result.Fail<Guid>(ex.Message);
        }
    }
}
