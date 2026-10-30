namespace Sankore.Modules.Workflow.Features.Templates.Promote;

using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;

/// <summary>
/// « Copier en production » for a workflow (sandbox lot 4) — the copy itself is
/// <see cref="WorkflowTemplateCopier"/>, which the refresh of a test mode (lot 5) runs the other way.
/// </summary>
internal sealed record PromoteWorkflowTemplateCommand(Guid TemplateId, bool DryRun)
    : IRequest<Result<PromotionResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowTemplate";
    public string? ResourceId => TemplateId.ToString();
}

internal sealed class PromoteWorkflowTemplateHandler(ICurrentUser me, WorkflowTemplateCopier copier)
    : IRequestHandler<PromoteWorkflowTemplateCommand, Result<PromotionResult>>
{
    public Task<Result<PromotionResult>> Handle(PromoteWorkflowTemplateCommand cmd, CancellationToken ct)
        => me.TenantId == me.TeamTenantId
            ? Task.FromResult(Result.Fail<PromotionResult>(PromotionErrors.TestModeRequired))
            : copier.CopyAsync(new ConfigurationCopy(me.TenantId, cmd.TemplateId, me.TeamTenantId, me.Id, me.DisplayName, cmd.DryRun, ToProduction: true), ct);
}

/// <summary>
/// One workflow version from one tenant to another, as a NEW version of the same circuit — the
/// entity type and circuit key name the family, as for « Créer un brouillon ». The clone is the
/// aggregate's own (<see cref="WorkflowTemplate.CopyTo"/>), with the template's triggers; roles and
/// agencies are the team's, shared by both modes, so the steps need no translation. One write in
/// one context: no replay.
///
/// <para>
/// <b>To production</b> the copy is INACTIVE: production's running instances keep their version, and
/// the copy takes over once somebody activates it there, after reading it. <b>To the test mode</b>
/// (a refresh) it is activated at once, retiring the twin's own active version of that circuit —
/// the sandbox must behave as production does. Either way, an inactive top version the target never
/// ran (no instance) is replaced rather than stacked: a second copy must not leave two drafts.
/// </para>
/// </summary>
internal sealed class WorkflowTemplateCopier(WorkflowDbContext db) : IConfigurationCopier
{
    public string Kind => "workflow";

    public int Order => 20;

    /// <summary>A refresh copies the versions in force — one per circuit.</summary>
    public async Task<IReadOnlyList<Guid>> ListAsync(Guid tenantId, CancellationToken ct)
        => await db.WorkflowTemplates.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.IsActive).Select(t => t.Id).ToListAsync(ct);

    public async Task<Result<PromotionResult>> CopyAsync(ConfigurationCopy copy, CancellationToken ct)
    {
        var source = await db.WorkflowTemplates.IgnoreQueryFilters()
            .Include(t => t.Steps).ThenInclude(s => s.Rules)
            .Include(t => t.Transitions)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == copy.SourceId && t.TenantId == copy.SourceTenantId, ct);
        if (source is null) return Result.Fail<PromotionResult>(PromotionErrors.SourceNotFound);

        // Children loaded: a replaced draft takes its steps, rules and transitions with it.
        var family = await db.WorkflowTemplates.IgnoreQueryFilters().AsTracking()
            .Include(t => t.Steps).ThenInclude(s => s.Rules)
            .Include(t => t.Transitions)
            .Where(t => t.TenantId == copy.TargetTenantId && t.EntityType == source.EntityType && t.Circuit == source.Circuit)
            .OrderByDescending(t => t.Version)
            .ToListAsync(ct);

        var top = family.FirstOrDefault();
        var replace = top is { IsActive: false }
            && !await db.WorkflowInstances.IgnoreQueryFilters().AnyAsync(i => i.TemplateId == top.Id, ct);
        var version = top is null ? 1 : replace ? top.Version : top.Version + 1;

        var action = family.Count == 0 ? PromotionActions.Created : PromotionActions.Updated;
        List<string> notes = copy.ToProduction
            ? [$"Copié en version {version}, inactive : activez-la en production pour qu’elle remplace la version en cours."]
            : [$"Copié en version {version} et activé dans le mode test."];
        if (source.Transitions.Count > 0)
            notes.Add("Les actions automatiques attachées aux transitions ne sont pas copiées, comme pour un brouillon.");

        if (copy.DryRun) return Result.Ok(new PromotionResult(action, replace ? top!.Id : null, [], notes));

        if (replace)
        {
            db.WorkflowTriggers.RemoveRange(await db.WorkflowTriggers.IgnoreQueryFilters().Where(t => t.TemplateId == top!.Id).ToListAsync(ct));
            db.WorkflowTemplates.Remove(top!);
        }

        var copied = source.CopyTo(copy.TargetTenantId, version, copy.UserId);
        if (!copy.ToProduction)
        {
            foreach (var active in family.Where(t => t.IsActive)) active.Deactivate();
            copied.Activate();
        }
        db.WorkflowTemplates.Add(copied);

        var triggers = await db.WorkflowTriggers.IgnoreQueryFilters().AsNoTracking().Where(t => t.TemplateId == source.Id).ToListAsync(ct);
        foreach (var trigger in triggers)
        {
            var clone = WorkflowTrigger.Create(copy.TargetTenantId, copied.Id, trigger.TriggerType, trigger.EventName, trigger.ConditionJson);
            if (!trigger.IsActive) clone.Deactivate();
            db.WorkflowTriggers.Add(clone);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(new PromotionResult(action, copied.Id, [], notes));
    }
}

internal static class PromoteWorkflowTemplateEndpoint
{
    public static IEndpointRouteBuilder MapPromoteWorkflowTemplate(this IEndpointRouteBuilder group)
    {
        group.MapPromote("WorkflowTemplate", Permissions.CanCreateWorkflow.Code,
            (id, dryRun, sender, ct) => sender.Send(new PromoteWorkflowTemplateCommand(id, dryRun), ct));
        return group;
    }
}
