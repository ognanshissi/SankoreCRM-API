namespace Sankore.Modules.Leads.Features.QualificationTemplates.ListQualificationTemplates;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Shared.Kernel;

/// <param name="Status">Filter by lifecycle status. Defaults to <see cref="TemplateStatus.Published"/>.</param>
/// <param name="ProductCategory">Optional product category filter.</param>
internal sealed record ListQualificationTemplatesQuery(
    TemplateStatus? Status,
    ProductCategory? ProductCategory = null)
    : IRequest<Result<IReadOnlyList<QualificationTemplateDto>>>;
