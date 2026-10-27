namespace Sankore.Modules.Leads.Features.Opportunities.MoveToStep;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Moves a project onto a step of ITS template — the one its product resolves to now. A step of
/// another template is refused rather than mapped: the screen drew this project's steps, so a
/// foreign id means the product changed under it, and guessing would move it somewhere nobody chose.
/// </summary>
internal sealed record MoveOpportunityToStepCommand(
    Guid OpportunityId,
    Guid StepId,
    /// <summary>UpdatedAt the caller last read; null skips the staleness check.</summary>
    DateTimeOffset? ExpectedUpdatedAt = null)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed class MoveOpportunityToStepHandler(
    LeadsDbContext db,
    ProjectTemplateResolver templates,
    ProjectStepTransition transition)
    : IRequestHandler<MoveOpportunityToStepCommand, Result>
{
    public async Task<Result> Handle(MoveOpportunityToStepCommand cmd, CancellationToken ct)
    {
        var opportunity = await db.Opportunities
            .AsTracking()
            .FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);

        if (opportunity is null)
            return Result.Fail(OpportunityErrors.NotFound);

        if (opportunity.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);

        var template = await templates.ForProjectAsync(opportunity, ct);
        var step = template.Steps.FirstOrDefault(s => s.Id == cmd.StepId);
        if (step is null)
            return Result.Fail(ProjectTemplateErrors.StepNotInTemplate);

        var moved = await transition.MoveAsync(opportunity, template, step, ct);
        if (moved.IsFailure)
            return moved;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

/// <param name="StepId">A step of the project's own template, as listed in its <c>steps</c>.</param>
public sealed record MoveOpportunityToStepRequest(
    Guid StepId,
    DateTimeOffset? ExpectedUpdatedAt = null);
