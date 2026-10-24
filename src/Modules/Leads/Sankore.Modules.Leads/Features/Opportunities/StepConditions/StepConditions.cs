namespace Sankore.Modules.Leads.Features.Opportunities.StepConditions;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

// ── Tick a checklist point ──────────────────────────────────────────────────────

/// <summary>Ticks or unticks one point of the project's template checklists.</summary>
internal sealed record SetProjectChecklistItemCommand(Guid OpportunityId, Guid ItemId, bool Done)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed class SetProjectChecklistItemHandler(
    LeadsDbContext db, ProjectTemplateResolver templates, TimeProvider clock)
    : IRequestHandler<SetProjectChecklistItemCommand, Result>
{
    public async Task<Result> Handle(SetProjectChecklistItemCommand cmd, CancellationToken ct)
    {
        var project = await db.Opportunities.AsTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null)
            return Result.Fail(OpportunityErrors.NotFound);

        // A point of ANOTHER template is refused: it would sit in the list, ticked for nothing.
        var template = await templates.ResolveAsync(project.Product, ct);
        if (!template.Steps.Any(s => (s.Checklist ?? []).Any(i => i.Id == cmd.ItemId)))
            return Result.Fail(ProjectTemplateErrors.ChecklistItemUnknown);

        var set = project.SetChecklistItem(cmd.ItemId, cmd.Done, clock.GetUtcNow());
        if (set.IsFailure)
            return set;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

// ── Run the current step's circuit again ────────────────────────────────────────

/// <summary>After a refusal: starts the current step's validation circuit again.</summary>
internal sealed record RestartProjectWorkflowCommand(Guid OpportunityId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed class RestartProjectWorkflowHandler(
    LeadsDbContext db, ProjectTemplateResolver templates, ProjectWorkflows workflows)
    : IRequestHandler<RestartProjectWorkflowCommand, Result>
{
    public async Task<Result> Handle(RestartProjectWorkflowCommand cmd, CancellationToken ct)
    {
        var project = await db.Opportunities.AsTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null)
            return Result.Fail(OpportunityErrors.NotFound);
        if (project.Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            return Result.Fail(OpportunityErrors.AlreadyClosed);

        var template = await templates.ResolveAsync(project.Product, ct);
        var step = template.CurrentStep(project.StepId, project.Stage);
        if (step is null)
            return Result.Fail(ProjectTemplateErrors.WorkflowNotRestartable);

        var restarted = await workflows.RestartAsync(project, step, ct);
        if (restarted.IsFailure)
            return restarted;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

/// <param name="Done">True ticks the point, false unticks it.</param>
public sealed record SetProjectChecklistItemRequest(bool Done);
