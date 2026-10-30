namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using System.Transactions;
using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;

/// <summary>
/// « Copier en production » for a project template (sandbox lot 4), replayed through the ordinary
/// create/update commands — so production records a new template VERSION and its projects move
/// on exactly as after an edit made there.
///
/// <para>
/// The production template is the one of the same SCOPE (product code, family, or the default).
/// Its steps are matched by LABEL and sent back with their production ids: the projects standing on
/// a step that kept its name stay on it. Checklist points are matched the same way inside a step.
/// Field keys are sent as the test mode has them, which keeps the values of a field production
/// already has and lets the steps keep requiring the same keys.
/// </para>
///
/// <para>
/// The product is a BLOCKING dependency (production refuses a template for a product it does not
/// have); a workflow circuit a step starts is not — production simply starts nothing until a circuit
/// of that key is active there — and both are reported for the front to copy first.
/// </para>
/// </summary>
// Audited like any save — in the test mode, by whoever copied; the replayed commands are audited
// again in production. ProductionReplay suppresses this command's transaction for the replay.
internal sealed record PromoteProjectTemplateCommand(Guid TemplateId, bool DryRun)
    : IRequest<Result<PromotionResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ProjectTemplate";
    public string? ResourceId => TemplateId.ToString();
}

internal sealed class PromoteProjectTemplateHandler(ProductionReplay production, ProjectTemplateCopier copier)
    : IRequestHandler<PromoteProjectTemplateCommand, Result<PromotionResult>>
{
    public Task<Result<PromotionResult>> Handle(PromoteProjectTemplateCommand cmd, CancellationToken ct)
        => production.InTestMode
            ? copier.CopyAsync(production.ToProduction(cmd.TemplateId, cmd.DryRun), ct)
            : Task.FromResult(Result.Fail<PromotionResult>(PromotionErrors.TestModeRequired));
}

/// <summary>One project template from one tenant to another — see the command for the rules.</summary>
internal sealed class ProjectTemplateCopier(
    LeadsDbContext db,
    IServiceScopeFactory scopes,
    IAdministrationModule administration,
    IWorkflowModule workflow) : IConfigurationCopier
{
    public string Kind => "project-template";

    /// <summary>After products and workflows, which a template names.</summary>
    public int Order => 30;

    public async Task<IReadOnlyList<Guid>> ListAsync(Guid tenantId, CancellationToken ct)
        => await db.ProjectTemplates.IgnoreQueryFilters().Where(t => t.TenantId == tenantId).Select(t => t.Id).ToListAsync(ct);

    public async Task<Result<PromotionResult>> CopyAsync(ConfigurationCopy copy, CancellationToken ct)
    {
        var source = await db.ProjectTemplates.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == copy.SourceId && t.TenantId == copy.SourceTenantId, ct);
        if (source is null) return Result.Fail<PromotionResult>(PromotionErrors.SourceNotFound);

        var target = await db.ProjectTemplates.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == copy.TargetTenantId)
            .FirstOrDefaultAsync(t => source.ProductCode != null
                ? t.ProductCode == source.ProductCode
                : t.ProductCode == null && t.Category == source.Category, ct);

        // Suppressed: M12 and the workflow module read through their own contexts, which would
        // promote the caller's transaction to a distributed one Npgsql refuses.
        List<PromotionDependency> missing;
        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            missing = await MissingAsync(source, copy, ct);
            suppressed.Complete();
        }
        var action = target is null ? PromotionActions.Created : PromotionActions.Updated;

        if (copy.DryRun) return Result.Ok(new PromotionResult(action, target?.Id, missing, []));
        if (missing.Any(m => m.Blocking)) return Result.Fail<PromotionResult>(PromotionErrors.DependencyMissing);

        var steps = source.Steps.Select(step =>
        {
            var match = target?.Steps.FirstOrDefault(s => SameLabel(s.Label, step.Label));
            return new ProjectStepInput(
                match?.Id, step.Label, step.Phase, step.Probability, step.Suggestion,
                step.OpensClientFile, step.IsApproval, step.RequiresQuestionnaires,
                step.RequiresAmount, step.RequiresCloseDate,
                step.Checklist.Select(i => new ProjectChecklistItemInput(
                    match?.Checklist.FirstOrDefault(c => SameLabel(c.Label, i.Label))?.Id, i.Label)).ToList(),
                step.WorkflowCircuit, step.RequiredFieldKeys);
        }).ToList();
        var fields = source.Fields.Select(f => new ProjectFieldInput(f.Key, f.Label, f.Type, f.HelpText, f.Options)).ToList();
        var forecast = new ProjectForecastOptions(source.TracksForecast, source.AmountKind, source.CriticalCloseDate);

        var copied = await TenantReplay.RunAsync<Result<Guid>>(scopes, copy.TargetTenantId, copy.UserId, copy.UserName, async (sender, _) =>
        {
            if (target is null)
                return await sender.Send(new CreateProjectTemplateCommand(
                    source.Name, source.Category, source.ProductCode, steps, forecast, fields), ct);

            var updated = await sender.Send(new UpdateProjectTemplateCommand(target.Id, source.Name, steps, forecast, fields), ct);
            return updated.IsFailure ? Result.Fail<Guid>(updated.Error!) : Result.Ok(target.Id);
        });

        return copied.IsFailure
            ? Result.Fail<PromotionResult>(copied.Error!)
            : Result.Ok(new PromotionResult(action, copied.Value, missing, []));
    }

    private async Task<List<PromotionDependency>> MissingAsync(ProjectTemplate source, ConfigurationCopy copy, CancellationToken ct)
    {
        List<PromotionDependency> missing = [];

        if (source.ProductCode is { } code
            && await administration.GetProductAsync(copy.TargetTenantId, code, ct) is null)
        {
            var mine = await administration.GetProductAsync(copy.SourceTenantId, code, ct);
            missing.Add(new PromotionDependency("product", mine?.Id ?? Guid.Empty, mine?.Name ?? code, Blocking: true));
        }

        var circuits = source.Steps.Select(s => s.WorkflowCircuit).OfType<string>().Distinct().ToList();
        if (circuits.Count > 0)
        {
            var there = await workflow.ListActiveCircuitsAsync(copy.TargetTenantId, ProjectWorkflows.EntityType, ct);
            var here = await workflow.ListActiveCircuitsAsync(copy.SourceTenantId, ProjectWorkflows.EntityType, ct);
            foreach (var circuit in circuits.Where(c => there.All(l => l.Circuit != c)))
            {
                var found = here.FirstOrDefault(m => m.Circuit == circuit);
                missing.Add(new PromotionDependency("workflow", found?.TemplateId ?? Guid.Empty, found?.Name ?? circuit, Blocking: false));
            }
        }

        return missing;
    }

    private static bool SameLabel(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

internal static class PromoteProjectTemplateEndpoint
{
    public static IEndpointRouteBuilder MapPromoteProjectTemplate(this IEndpointRouteBuilder group)
    {
        group.MapPromote("ProjectTemplate", Permissions.CanManagePipelineStages.Code,
            (id, dryRun, sender, ct) => sender.Send(new PromoteProjectTemplateCommand(id, dryRun), ct));
        return group;
    }
}
