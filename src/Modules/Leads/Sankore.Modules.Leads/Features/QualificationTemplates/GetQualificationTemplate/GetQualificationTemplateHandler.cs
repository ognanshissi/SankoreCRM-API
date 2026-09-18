namespace Sankore.Modules.Leads.Features.QualificationTemplates.GetQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetQualificationTemplateHandler(LeadsDbContext db)
    : IRequestHandler<GetQualificationTemplateQuery, Result<QualificationTemplateDto>>
{
    public async Task<Result<QualificationTemplateDto>> Handle(
        GetQualificationTemplateQuery query, CancellationToken ct)
    {
        var template = await db.QualificationTemplates
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == query.TemplateId, ct);

        if (template is null)
            return Result.Fail<QualificationTemplateDto>("TEMPLATE_NOT_FOUND");

        return Result.Ok(new QualificationTemplateDto(
            template.Id,
            template.Name,
            template.Description,
            template.ProductName,
            template.IsActive,
            template.CreatedAt,
            template.Questions
                .OrderBy(q => q.Order)
                .Select(q => new QualificationQuestionDto(
                    q.Id, q.Label, q.Type.ToString(),
                    q.GetOptions(), q.Weight, q.IsRequired, q.Order))
                .ToList()));
    }
}
