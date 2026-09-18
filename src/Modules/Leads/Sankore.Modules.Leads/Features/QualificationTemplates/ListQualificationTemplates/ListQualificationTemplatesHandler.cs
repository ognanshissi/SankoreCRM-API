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
            .Include(t => t.Questions)
            .AsQueryable();

        if (query.ActiveOnly)
            templatesQuery = templatesQuery.Where(t => t.IsActive);

        var templates = await templatesQuery
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var dtos = templates.Select(t => new QualificationTemplateDto(
            t.Id,
            t.Name,
            t.Description,
            t.ProductName,
            t.IsActive,
            t.CreatedAt,
            t.Questions
                .OrderBy(q => q.Order)
                .Select(q => new QualificationQuestionDto(
                    q.Id, q.Label, q.Type.ToString(),
                    q.GetOptions(), q.Weight, q.IsRequired, q.Order))
                .ToList()))
            .ToList();

        return Result.Ok<IReadOnlyList<QualificationTemplateDto>>(dtos);
    }
}
