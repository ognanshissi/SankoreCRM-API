namespace Sankore.Modules.Leads.Features.Opportunities.TemplateVersion;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Moves a project onto the latest version of its template (product owner, 2026-10-26: on demand,
/// from the project's page). It stays on its step when the new version still has it, else lands on
/// the step of its phase; the new version's conditions apply from the next move on.
/// </summary>
/// <remarks>
/// An ENTRY, like a new project's first step, not a move: the « to reach » conditions of the step
/// it lands on are not checked — it is already there — and no circuit is started.
/// </remarks>
internal sealed record FollowLatestTemplateVersionCommand(Guid OpportunityId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed class FollowLatestTemplateVersionHandler(
    LeadsDbContext db, ProjectTemplateResolver templates, TimeProvider clock)
    : IRequestHandler<FollowLatestTemplateVersionCommand, Result>
{
    public const string AlreadyLatest = "PROJECT_TEMPLATE_VERSION_ALREADY_LATEST";

    public async Task<Result> Handle(FollowLatestTemplateVersionCommand cmd, CancellationToken ct)
    {
        var project = await db.Opportunities.AsTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null)
            return Result.Fail(OpportunityErrors.NotFound);
        if (project.Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            return Result.Fail(OpportunityErrors.AlreadyClosed);

        var current = await templates.ForProjectAsync(project, ct);
        var latest = await templates.ResolveAsync(project.Product, ct);

        // Same template, same version: nothing newer to follow. A project whose template was
        // deleted already reads the product's template today, which is the latest.
        if (current.Id == latest.Id && (latest.Version is null || current.Version == latest.Version))
            return Result.Fail(AlreadyLatest);

        var now = clock.GetUtcNow();
        var step = latest.CurrentStep(project.StepId, project.Stage);
        project.FollowTemplateVersion(latest.AttachableVersionId, now);
        if (step is not null)
            project.MoveToStep(step.Id, step.Phase, step.Probability, now);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
