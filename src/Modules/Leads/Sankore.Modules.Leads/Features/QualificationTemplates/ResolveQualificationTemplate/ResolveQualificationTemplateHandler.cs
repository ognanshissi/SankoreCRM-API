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
        var code = query.ProductCode.Trim().ToUpperInvariant();

        // ── Level 1: product-specific template ──────────────────────────
        var template = await db.QualificationTemplates
            .Include(t => t.Sections)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(
                t => t.ProductCode == code && t.Status == TemplateStatus.Published, ct);

        if (template is not null)
            return Result.Ok(QualificationTemplateMappings.ToDto(template));

        // ── Level 2: category-level template ────────────────────────────
        // Look up the product's category from the Administration module
        var categoryStr = await admin.GetProductCategoryAsync(
            tenant.CurrentTenantId, code, ct);

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
