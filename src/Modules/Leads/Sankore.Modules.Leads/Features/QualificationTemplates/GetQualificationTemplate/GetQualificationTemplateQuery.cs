namespace Sankore.Modules.Leads.Features.QualificationTemplates.GetQualificationTemplate;

using MediatR;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Shared.Kernel;

internal sealed record GetQualificationTemplateQuery(Guid TemplateId)
    : IRequest<Result<QualificationTemplateDto>>;
