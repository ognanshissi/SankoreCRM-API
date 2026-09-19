namespace Sankore.Modules.Leads.Features.QualificationTemplates.ListQualificationTemplates;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Shared.Kernel;

/// <param name="Status">Filter by lifecycle status. Defaults to <see cref="TemplateStatus.Published"/>.</param>
/// <param name="ProductType">Optional product type filter.</param>
internal sealed record ListQualificationTemplatesQuery(
    TemplateStatus? Status = TemplateStatus.Published,
    ProductType? ProductType = null)
    : IRequest<Result<IReadOnlyList<QualificationTemplateDto>>>;
