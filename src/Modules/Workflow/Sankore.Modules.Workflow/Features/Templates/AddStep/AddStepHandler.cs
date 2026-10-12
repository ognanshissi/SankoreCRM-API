using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.AddStep;

internal sealed class AddStepHandler(WorkflowDbContext db)
    : IRequestHandler<AddStepCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddStepCommand request, CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail<Guid>($"Template {request.TemplateId} not found.");

        if (template.IsActive)
            return Result.Fail<Guid>("Cannot modify steps of an active template. Deactivate it first.");

        try
        {
            var needsShift = request.InsertAt && template.Steps.Any(s => s.Order == request.Order);

            // Insertion au milieu : deux enregistrements dans une même transaction, l'index unique
            // (TemplateId, Order) interdisant un décalage en une passe (voir ParkStepsFrom).
            await using var tx = needsShift && db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct)
                : null;

            if (needsShift)
            {
                var parked = template.ParkStepsFrom(request.Order);
                await db.SaveChangesAsync(ct);
                template.UnparkSteps(parked);
            }

            var step = template.AddStep(
                request.Order,
                request.Name,
                request.Description,
                request.ApproverRoleCode,
                request.TimeoutHours,
                request.Kind,
                request.ConfigJson,
                request.IsBlocking);

            // Explicitly register the new step entity so EF change tracker picks it
            // up regardless of backing-field access mode (required for InMemory tests).
            db.WorkflowStepDefinitions.Add(step);
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return Result.Ok(step.Id);
        }
        catch (DomainException ex)
        {
            return Result.Fail<Guid>(ex.Message);
        }
    }
}
