namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>A step as the screens and the move handler see it, stored or built in.</summary>
public sealed record ProjectStepDto(
    Guid Id,
    string Label,
    OpportunityStage Phase,
    int Probability,
    string? Suggestion,
    bool OpensClientFile = false,
    bool IsApproval = false,
    bool RequiresQuestionnaires = false,
    bool RequiresAmount = false,
    bool RequiresCloseDate = false,
    IReadOnlyList<ProjectChecklistItem>? Checklist = null,
    /// <summary>The M12 circuit started on reaching the step; null when none.</summary>
    string? WorkflowCircuit = null,
    /// <summary>Keys of the template's fields that must hold a value to reach the step.</summary>
    IReadOnlyList<string>? RequiredFieldKeys = null);

/// <summary>
/// The template that applies to one project, and where it came from.
/// </summary>
/// <param name="Id">Null for the built-in default, which is not stored.</param>
/// <param name="Scope">« Product », « Category » or « Default ».</param>
/// <param name="TracksForecast">The projects show a probability and an expected close date — see <c>ProjectTemplate.TracksForecast</c>.</param>
/// <param name="AmountKind">What the project's amount stands for — see <c>ProjectTemplate.AmountKind</c>.</param>
/// <param name="CriticalCloseDate">The close date is a deadline — see <c>ProjectTemplate.CriticalCloseDate</c>.</param>
/// <param name="VersionId">The version read: what a new project is attached to. Null for a template never saved since versions exist (its live state IS its version 1) and for the built-in.</param>
/// <param name="Version">Its number; null as above.</param>
/// <param name="LatestVersion">The template's latest number, to offer a project still on an older one the move.</param>
public sealed record ResolvedProjectTemplate(
    Guid? Id,
    string Name,
    string Scope,
    IReadOnlyList<ProjectStepDto> Steps,
    bool TracksForecast = true,
    ProjectAmountKind AmountKind = ProjectAmountKind.Generic,
    bool CriticalCloseDate = false,
    IReadOnlyList<ProjectFieldDefinition>? Fields = null,
    Guid? VersionId = null,
    int? Version = null,
    int? LatestVersion = null)
{
    /// <summary>What a project created or re-attached on this template stores.</summary>
    public Guid? AttachableVersionId => Id is null ? ProjectTemplate.BuiltInVersionId : VersionId;

    /// <summary>
    /// The step a project stands on: its own when this template still has it, else the last step
    /// whose phase is not past the project's — so a project moved by phase only, or whose step was
    /// removed or whose product changed, lands on the nearest step that does not overstate it.
    /// A closed project stands on none.
    /// </summary>
    public ProjectStepDto? CurrentStep(Guid? stepId, OpportunityStage stage)
    {
        if (stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            return null;

        if (stepId is { } id && Steps.FirstOrDefault(s => s.Id == id) is { } own)
            return own;

        var phase = ProjectTemplate.OpenPhases.ToList().IndexOf(stage);
        return Steps.LastOrDefault(s => ProjectTemplate.OpenPhases.ToList().IndexOf(s.Phase) <= phase)
            ?? Steps.FirstOrDefault();
    }
}

/// <summary>
/// Finds a project's template from its product: the product line's own, else its family's, else
/// the tenant's default, else the built-in one. The same three levels as
/// <c>ResolveQualificationTemplateHandler</c>, over a product that is FREE TEXT on the project —
/// hence the catalogue lookup, which never matches fuzzily.
///
/// <para>
/// <b>Scoped, and memoised per label</b>: a list of forty projects on six products costs six
/// resolutions and one read of the templates, not forty of each.
/// </para>
/// <para>
/// <b>Two questions, two methods.</b> <see cref="ResolveAsync"/> answers « which template would a
/// project on this product follow TODAY » — the latest version, for a creation form or a new
/// project. <see cref="ForProjectAsync"/> answers « what does THIS project follow » — the version
/// it is attached to (product owner, 2026-10-26). Anything that gates, places or describes an
/// existing project must ask the second one, or editing a template would reach it.
/// </para>
/// </summary>
internal sealed class ProjectTemplateResolver(
    LeadsDbContext db,
    IAdministrationModule administration,
    ITenantContext tenant)
{
    private List<ProjectTemplate>? _templates;
    private IReadOnlyList<ProductSummary>? _catalogue;
    private readonly Dictionary<string, ResolvedProjectTemplate> _byLabel = new(StringComparer.Ordinal);
    private List<ProjectTemplateVersion>? _versions;

    public async Task<ResolvedProjectTemplate> ResolveAsync(string? product, CancellationToken ct)
    {
        var key = product?.Trim() ?? string.Empty;
        if (_byLabel.TryGetValue(key, out var known))
            return known;

        var templates = _templates ??= await db.ProjectTemplates.AsNoTracking().ToListAsync(ct);
        var (code, category) = await ProductOfAsync(key, ct);

        var template =
            (code is null ? null : templates.FirstOrDefault(t => t.ProductCode == code))
            ?? (category is null ? null : templates.FirstOrDefault(t => t.ProductCode is null && t.Category == category))
            ?? templates.FirstOrDefault(t => t.IsDefault);

        var resolved = template is null ? BuiltIn() : await LatestAsync(template, ct);
        _byLabel[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// What a project follows: the version it is attached to; for a project attached to none, its
    /// template's version 1 — the state its template was in before versions, see
    /// <see cref="ProjectTemplateVersion"/>. A version whose template was deleted falls back to the
    /// product's template today, as every project did before versions.
    /// </summary>
    public Task<ResolvedProjectTemplate> ForProjectAsync(Opportunity project, CancellationToken ct)
        => ForProjectAsync(project.TemplateVersionId, project.Product, ct);

    public async Task<ResolvedProjectTemplate> ForProjectAsync(Guid? versionId, string? product, CancellationToken ct)
    {
        if (versionId == ProjectTemplate.BuiltInVersionId)
            return BuiltIn();

        var templates = _templates ??= await db.ProjectTemplates.AsNoTracking().ToListAsync(ct);
        var versions = await VersionsAsync(ct);

        if (versionId is { } id && versions.FirstOrDefault(v => v.Id == id) is { } pinned
            && templates.FirstOrDefault(t => t.Id == pinned.TemplateId) is { } owner)
            return From(owner, pinned, LatestOf(versions, owner.Id));

        var today = await ResolveAsync(product, ct);
        if (versionId is not null || today.Id is not { } templateId)
            return today;

        var first = versions.FirstOrDefault(v => v.TemplateId == templateId && v.Number == 1);
        var template = templates.First(t => t.Id == templateId);
        return first is null ? today : From(template, first, LatestOf(versions, templateId));
    }

    private async Task<ResolvedProjectTemplate> LatestAsync(ProjectTemplate template, CancellationToken ct)
    {
        var versions = await VersionsAsync(ct);
        var latest = versions.Where(v => v.TemplateId == template.Id).MaxBy(v => v.Number);
        // The live template and its latest version say the same thing — every save writes both —
        // so the live one is read and the version only names it.
        return From(template) with { VersionId = latest?.Id, Version = latest?.Number, LatestVersion = latest?.Number };
    }

    private async Task<List<ProjectTemplateVersion>> VersionsAsync(CancellationToken ct)
        => _versions ??= await db.ProjectTemplateVersions.AsNoTracking().ToListAsync(ct);

    private static int? LatestOf(IEnumerable<ProjectTemplateVersion> versions, Guid templateId)
        => versions.Where(v => v.TemplateId == templateId).Select(v => (int?)v.Number).Max();

    private static string ScopeOf(ProjectTemplate template)
        => template.ProductCode is not null ? "Product" : template.Category is not null ? "Category" : "Default";

    public static ResolvedProjectTemplate From(ProjectTemplate template)
        => From(template.Id, ScopeOf(template), ProjectTemplateSnapshot.Of(template));

    private static ResolvedProjectTemplate From(ProjectTemplate template, ProjectTemplateVersion version, int? latest)
        => From(template.Id, ScopeOf(template), version.Read()) with
        {
            VersionId = version.Id,
            Version = version.Number,
            LatestVersion = latest,
        };

    private static ResolvedProjectTemplate From(Guid id, string scope, ProjectTemplateSnapshot snapshot) => new(
        id,
        snapshot.Name,
        scope,
        snapshot.Steps.Select(s => new ProjectStepDto(
            s.Id, s.Label, s.Phase, s.Probability, s.Suggestion, s.OpensClientFile, s.IsApproval,
            s.RequiresQuestionnaires, s.RequiresAmount, s.RequiresCloseDate, s.Checklist, s.WorkflowCircuit,
            s.RequiredFieldKeys)).ToList(),
        snapshot.TracksForecast,
        snapshot.AmountKind,
        snapshot.CriticalCloseDate,
        snapshot.Fields);

    public static ResolvedProjectTemplate BuiltIn() => new(
        null,
        ProjectTemplate.BuiltInDefaultName,
        "Default",
        ProjectTemplate.BuiltInDefault
            .Select(s => new ProjectStepDto(s.Id!.Value, s.Label, s.Phase, s.Probability, s.Suggestion))
            .ToList());

    /// <summary>
    /// The catalogue line a project's product names, as (code, category) — both null when it names
    /// none, which is the ordinary case of a need « à préciser ».
    /// </summary>
    private async Task<(string? Code, ProductCategory? Category)> ProductOfAsync(string label, CancellationToken ct)
    {
        if (label.Length == 0)
            return (null, null);

        // Suppressed: M12 reads through its own DbContext, and a second connection enlisting in a
        // command's ambient scope promotes it to a distributed transaction Npgsql refuses.
        using var suppressed = new TransactionScope(
            TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        var lineId = await administration.ResolveProductLineAsync(tenant.CurrentTenantId, label, ct);
        if (lineId is null)
            return (null, null);

        _catalogue ??= await administration.ListProductsAsync(tenant.CurrentTenantId, null, ct);
        var line = _catalogue.FirstOrDefault(p => p.Id == lineId.Value);

        suppressed.Complete();

        if (line is null)
            return (null, null);

        return (
            line.Code.Trim().ToUpperInvariant(),
            Enum.TryParse<ProductCategory>(line.Category, ignoreCase: true, out var category) ? category : null);
    }
}
