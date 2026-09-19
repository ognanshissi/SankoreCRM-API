namespace Sankore.Modules.Leads.Features.QualificationTemplates.ListQualificationTemplates;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListQualificationTemplatesHandler(LeadsDbContext db)
    : IRequestHandler<ListQualificationTemplatesQuery, Result<IReadOnlyList<QualificationTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<QualificationTemplateDto>>> Handle(
        ListQualificationTemplatesQuery query, CancellationToken ct)
    {
        var templatesQuery = db.QualificationTemplates
            .Include(t => t.Sections)
            .Include(t => t.Questions)
            .AsQueryable();

        if (query.Status.HasValue)
            templatesQuery = templatesQuery.Where(t => t.Status == query.Status.Value);

        if (query.ProductType.HasValue)
            templatesQuery = templatesQuery.Where(t => t.ProductType == query.ProductType.Value);

        var templates = await templatesQuery
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var dtos = templates.Select(QualificationTemplateMappings.ToDto).ToList();

        return Result.Ok<IReadOnlyList<QualificationTemplateDto>>(dtos);
    }
}
