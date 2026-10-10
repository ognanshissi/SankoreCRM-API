namespace Sankore.Modules.Leads.Features.QualificationTemplates.ResolveQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ResolveQualificationTemplateHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin)
    : IRequestHandler<ResolveQualificationTemplateQuery, Result<QualificationTemplateDto>>
{
    public async Task<Result<QualificationTemplateDto>> Handle(
        ResolveQualificationTemplateQuery query, CancellationToken ct)
    {
        // A resolved product line wins over the label (BNF-B-01). One cached read gives both the
        // authoritative code and the category, which also saves the GetProductCategoryAsync
        // lookup at level 2 — and avoids the case where a label spells a code the catalogue does
        // not have, levels 1 and 2 both miss, and every lead quietly gets the generic template.
        string code;
        string? categoryStr = null;

        if (query.ProductLineId is { } lineId && lineId != Guid.Empty)
        {
            var line = await admin.GetBeneficiaryPolicyAsync(tenant.CurrentTenantId, lineId, ct);

            // A dangling line id falls back to the label rather than failing: BNF-B-01 makes the
            // reference opaque precisely so a retired or deleted product degrades gracefully.
            code        = line?.ProductCode ?? query.ProductCode.Trim().ToUpperInvariant();
            categoryStr = line?.Category;
        }
        else
        {
            code = query.ProductCode.Trim().ToUpperInvariant();
        }

        // ── Level 1: product-specific template ──────────────────────────
        var template = await db.QualificationTemplates
            .Include(t => t.Sections)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(
                t => t.ProductCode == code && t.Status == TemplateStatus.Published, ct);

        if (template is not null)
            return Result.Ok(QualificationTemplateMappings.ToDto(template));

        // ── Level 2: category-level template ────────────────────────────
        // Already known when the caller supplied a product line; otherwise looked up by code.
        categoryStr ??= await admin.GetProductCategoryAsync(tenant.CurrentTenantId, code, ct);

        if (categoryStr is not null)
        {
            template = await db.QualificationTemplates
                .Include(t => t.Sections)
                .Include(t => t.Questions)
                .FirstOrDefaultAsync(
                    t => t.ProductCategory.ToString() == categoryStr
                      && t.ProductCode == null
                      && t.Status == TemplateStatus.Published, ct);

            if (template is not null)
                return Result.Ok(QualificationTemplateMappings.ToDto(template));
        }

        // ── Level 3: generic template (no product category, no product code) ─
        template = await db.QualificationTemplates
            .Include(t => t.Sections)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(
                t => t.ProductCategory == null
                  && t.ProductCode == null
                  && t.Status == TemplateStatus.Published, ct);

        if (template is not null)
            return Result.Ok(QualificationTemplateMappings.ToDto(template));

        return Result.Fail<QualificationTemplateDto>("NO_TEMPLATE_FOUND");
    }
}
