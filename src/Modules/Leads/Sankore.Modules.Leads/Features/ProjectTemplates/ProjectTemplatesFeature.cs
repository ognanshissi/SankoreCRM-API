namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>A template as the settings screen lists it.</summary>
/// <param name="Id">Null for the built-in default: saving it creates the stored copy.</param>
/// <param name="Scope">« Default », « Category » or « Product ».</param>
/// <param name="ProductName">The catalogue's name for <paramref name="ProductCode"/>, when it still has one.</param>
public sealed record ProjectTemplateDto(
    Guid? Id,
    string Name,
    string Scope,
    ProductCategory? Category,
    string? ProductCode,
    string? ProductName,
    bool IsBuiltIn,
    IReadOnlyList<ProjectStepDto> Steps,
    DateTimeOffset? UpdatedAt,
    /// <summary>Whether the projects carry a probability and an expected close date.</summary>
    bool TracksForecast = true,
    /// <summary>What a project's amount stands for: the forecast never adds two kinds together.</summary>
    ProjectAmountKind AmountKind = ProjectAmountKind.Generic,
    /// <summary>The close date is a deadline: required, and an urgent task once it has passed.</summary>
    bool CriticalCloseDate = false,
    /// <summary>The fields its projects carry; a step names those needed to reach it.</summary>
    IReadOnlyList<ProjectFieldDefinition>? Fields = null,
    /// <summary>The latest version, which new projects follow; null before the first save since versions.</summary>
    int? Version = null);

// ── List ────────────────────────────────────────────────────────────────────────

internal sealed record ListProjectTemplatesQuery : IRequest<Result<IReadOnlyList<ProjectTemplateDto>>>;

/// <summary>
/// The default first — stored, or the built-in one when the tenant never saved it — then the
/// families, then the products. The built-in default is listed so the screen always has a
/// starting point to edit, and so its fixed step ids travel back on the first save.
/// </summary>
internal sealed class ListProjectTemplatesHandler(
    LeadsDbContext db,
    IAdministrationModule administration,
    ITenantContext tenant)
    : IRequestHandler<ListProjectTemplatesQuery, Result<IReadOnlyList<ProjectTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<ProjectTemplateDto>>> Handle(
        ListProjectTemplatesQuery query, CancellationToken ct)
    {
        var templates = await db.ProjectTemplates.AsNoTracking().ToListAsync(ct);
        var latest = await db.ProjectTemplateVersions.AsNoTracking()
            .GroupBy(v => v.TemplateId)
            .Select(g => new { TemplateId = g.Key, Number = g.Max(v => v.Number) })
            .ToDictionaryAsync(v => v.TemplateId, v => v.Number, ct);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (templates.Any(t => t.ProductCode is not null))
        {
            foreach (var p in await administration.ListProductsAsync(tenant.CurrentTenantId, null, ct))
                names[p.Code] = p.Name;
        }

        var result = new List<ProjectTemplateDto>();

        var stored = templates.FirstOrDefault(t => t.IsDefault);
        if (stored is null)
        {
            var builtIn = ProjectTemplateResolver.BuiltIn();
            result.Add(new ProjectTemplateDto(
                null, builtIn.Name, "Default", null, null, null, true, builtIn.Steps, null, builtIn.TracksForecast));
        }
        else
        {
            result.Add(ToDto(stored, null, latest));
        }

        result.AddRange(templates
            .Where(t => t.ProductCode is null && t.Category is not null)
            .OrderBy(t => t.Category.ToString())
            .Select(t => ToDto(t, null, latest)));

        result.AddRange(templates
            .Where(t => t.ProductCode is not null)
            .OrderBy(t => t.ProductCode)
            .Select(t => ToDto(t, names.GetValueOrDefault(t.ProductCode!), latest)));

        return Result.Ok<IReadOnlyList<ProjectTemplateDto>>(result);
    }

    private static ProjectTemplateDto ToDto(
        ProjectTemplate t, string? productName, Dictionary<Guid, int> latest)
    {
        var resolved = ProjectTemplateResolver.From(t);
        return new ProjectTemplateDto(
            t.Id, t.Name, resolved.Scope, t.Category, t.ProductCode, productName, false, resolved.Steps, t.UpdatedAt,
            t.TracksForecast, t.AmountKind, t.CriticalCloseDate, t.Fields,
            latest.TryGetValue(t.Id, out var v) ? v : null);
    }
}

// ── Create ──────────────────────────────────────────────────────────────────────

/// <summary>Neither scope: the tenant's default. One of them: a family's or a product's.</summary>
internal sealed record CreateProjectTemplateCommand(
    string Name,
    ProductCategory? Category,
    string? ProductCode,
    IReadOnlyList<ProjectStepInput> Steps,
    ProjectForecastOptions? Forecast = null,
    IReadOnlyList<ProjectFieldInput>? Fields = null)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "ProjectTemplate";
    public string? ResourceId => null;
}

internal sealed class CreateProjectTemplateHandler(
    LeadsDbContext db,
    IAdministrationModule administration,
    ITenantContext tenant,
    TimeProvider clock)
    : IRequestHandler<CreateProjectTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateProjectTemplateCommand cmd, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(cmd.ProductCode) ? null : cmd.ProductCode.Trim().ToUpperInvariant();
        ProductSummary? product = null;

        if (code is not null)
        {
            // Suppressed: M12 reads through its own DbContext, inside this command's scope.
            using (var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                product = await administration.GetProductAsync(tenant.CurrentTenantId, code, ct);
                suppressed.Complete();
            }

            if (product is null)
                return Result.Fail<Guid>(ProjectTemplateErrors.UnknownProduct);
        }

        var taken = await db.ProjectTemplates.AnyAsync(t =>
            code != null ? t.ProductCode == code
            : cmd.Category != null ? t.ProductCode == null && t.Category == cmd.Category
            : t.ProductCode == null && t.Category == null, ct);

        if (taken)
            return Result.Fail<Guid>(ProjectTemplateErrors.ScopeTaken);

        var created = ProjectTemplate.Create(
            tenant.CurrentTenantId, cmd.Name, cmd.Category, code, cmd.Steps, clock.GetUtcNow(), cmd.Forecast, cmd.Fields);
        if (created.IsFailure)
            return Result.Fail<Guid>(created.Error!);

        if (cmd.Steps.Any(s => s.RequiresQuestionnaires)
            && !await QuestionnaireFormAvailability.ExistsAsync(db, code, ScopeCategory(cmd.Category, product), ct))
            return Result.Fail<Guid>(ProjectTemplateErrors.QuestionnaireFormMissing);

        db.ProjectTemplates.Add(created.Value!);
        await ProjectTemplateVersioning.RecordAsync(db, created.Value!, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok(created.Value!.Id);
    }

    private static ProductCategory? ScopeCategory(ProductCategory? category, ProductSummary? product)
        => category ?? (product is not null && Enum.TryParse<ProductCategory>(product.Category, true, out var c) ? c : null);
}

// ── Update ──────────────────────────────────────────────────────────────────────

/// <summary>The scope never changes: a family's template does not become a product's.</summary>
internal sealed record UpdateProjectTemplateCommand(
    Guid TemplateId,
    string Name,
    IReadOnlyList<ProjectStepInput> Steps,
    ProjectForecastOptions? Forecast = null,
    IReadOnlyList<ProjectFieldInput>? Fields = null)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProjectTemplate";
    public string? ResourceId => TemplateId.ToString();
}

internal sealed class UpdateProjectTemplateHandler(
    LeadsDbContext db,
    TimeProvider clock,
    IAdministrationModule? administration = null,
    ITenantContext? tenant = null,
    ProjectTemplateResolver? templates = null)
    : IRequestHandler<UpdateProjectTemplateCommand, Result>
{
    public async Task<Result> Handle(UpdateProjectTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.ProjectTemplates
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);

        if (template is null)
            return Result.Fail(ProjectTemplateErrors.NotFound);

        var now = clock.GetUtcNow();
        await ProjectTemplateVersioning.KeepBaselineAsync(db, template, now, ct, templates);

        var updated = template.Update(cmd.Name, cmd.Steps, now, cmd.Forecast, cmd.Fields);
        if (updated.IsFailure)
            return updated;

        if (cmd.Steps.Any(s => s.RequiresQuestionnaires))
        {
            var category = template.Category ?? await ProductCategoryAsync(template.ProductCode, ct);
            if (!await QuestionnaireFormAvailability.ExistsAsync(db, template.ProductCode, category, ct))
                return Result.Fail(ProjectTemplateErrors.QuestionnaireFormMissing);
        }

        await ProjectTemplateVersioning.RecordAsync(db, template, now, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    /// <summary>The family of a product template's product; null when the catalogue cannot say.</summary>
    private async Task<ProductCategory?> ProductCategoryAsync(string? code, CancellationToken ct)
    {
        if (code is null || administration is null || tenant is null) return null;

        // Suppressed: M12 reads through its own DbContext, inside this command's scope.
        using var suppressed = new TransactionScope(
            TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        var product = await administration.GetProductAsync(tenant.CurrentTenantId, code, ct);
        suppressed.Complete();

        return product is not null && Enum.TryParse<ProductCategory>(product.Category, true, out var c) ? c : null;
    }
}

// ── Questionnaire marker ─────────────────────────────────────────────────────────

/// <summary>
/// Whether a published qualification form applies to a template's products — the levels the
/// questionnaire plan reads: the product's own, then its family's. Without one, a step marked
/// « Questionnaires remplis » would let every project through on nothing (product owner,
/// 2026-10-26). A generic form is counted for completeness, but the column holding the family is
/// required, so none exists: the tenant's default template, which names no product, therefore
/// cannot carry the marker.
/// </summary>
internal static class QuestionnaireFormAvailability
{
    public static Task<bool> ExistsAsync(
        LeadsDbContext db, string? productCode, ProductCategory? category, CancellationToken ct)
        => db.QualificationTemplates.AnyAsync(q =>
            q.Status == TemplateStatus.Published
            && ((q.ProductCode == null && q.ProductCategory == null)
                || (productCode != null && q.ProductCode == productCode)
                || (category != null && q.ProductCode == null && q.ProductCategory == category)), ct);
}

// ── Delete ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Removes a template. Its projects are not touched: they resolve to the next level down — the
/// family's, the default, or the built-in one — and stand on the step of their phase there.
/// Deleting the stored default is how a tenant returns to the built-in one.
/// </summary>
internal sealed record DeleteProjectTemplateCommand(Guid TemplateId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProjectTemplate";
    public string? ResourceId => TemplateId.ToString();
}

internal sealed class DeleteProjectTemplateHandler(LeadsDbContext db)
    : IRequestHandler<DeleteProjectTemplateCommand, Result>
{
    public async Task<Result> Handle(DeleteProjectTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.ProjectTemplates
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);

        if (template is null)
            return Result.Fail(ProjectTemplateErrors.NotFound);

        db.ProjectTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
