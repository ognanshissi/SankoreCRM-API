namespace Sankore.Modules.Leads.Features.QualificationTemplates.ListQualificationTemplates;

using MediatR;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Shared.Kernel;

internal sealed record ListQualificationTemplatesQuery(bool ActiveOnly = true)
    : IRequest<Result<IReadOnlyList<QualificationTemplateDto>>>;
