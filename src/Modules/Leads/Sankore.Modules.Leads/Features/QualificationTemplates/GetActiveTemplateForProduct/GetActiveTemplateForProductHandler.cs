namespace Sankore.Modules.Leads.Features.QualificationTemplates.GetActiveTemplateForProduct;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetActiveTemplateForProductHandler(LeadsDbContext db)
    : IRequestHandler<GetActiveTemplateForProductQuery, Result<QualificationTemplateDto>>
{
    public async Task<Result<QualificationTemplateDto>> Handle(
        GetActiveTemplateForProductQuery query, CancellationToken ct)
    {
        var template = await db.QualificationTemplates
            .Include(t => t.Sections)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(
                t => t.ProductCategory == query.ProductCategory && t.Status == TemplateStatus.Published, ct);

        if (template is null)
            return Result.Fail<QualificationTemplateDto>("NO_ACTIVE_TEMPLATE_FOR_PRODUCT");

        return Result.Ok(QualificationTemplateMappings.ToDto(template));
    }
}
