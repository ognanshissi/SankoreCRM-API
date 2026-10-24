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
    bool RequiresQuestionnaires = false);

/// <summary>
/// The template that applies to one project, and where it came from.
/// </summary>
/// <param name="Id">Null for the built-in default, which is not stored.</param>
/// <param name="Scope">« Product », « Category » or « Default ».</param>
public sealed record ResolvedProjectTemplate(
    Guid? Id,
    string Name,
    string Scope,
    IReadOnlyList<ProjectStepDto> Steps)
{
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
/// </summary>
internal sealed class ProjectTemplateResolver(
    LeadsDbContext db,
    IAdministrationModule administration,
    ITenantContext tenant)
{
    private List<ProjectTemplate>? _templates;
    private IReadOnlyList<ProductSummary>? _catalogue;
    private readonly Dictionary<string, ResolvedProjectTemplate> _byLabel = new(StringComparer.Ordinal);

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

        var resolved = template is null ? BuiltIn() : From(template);
        _byLabel[key] = resolved;
        return resolved;
    }

    public static ResolvedProjectTemplate From(ProjectTemplate template) => new(
        template.Id,
        template.Name,
        template.ProductCode is not null ? "Product" : template.Category is not null ? "Category" : "Default",
        template.Steps.Select(s => new ProjectStepDto(
            s.Id, s.Label, s.Phase, s.Probability, s.Suggestion, s.OpensClientFile, s.IsApproval,
            s.RequiresQuestionnaires)).ToList());

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
