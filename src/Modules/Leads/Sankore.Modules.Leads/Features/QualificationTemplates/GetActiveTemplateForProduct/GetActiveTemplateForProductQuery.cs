namespace Sankore.Modules.Leads.Features.QualificationTemplates.GetActiveTemplateForProduct;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Shared.Kernel;

/// <summary>Returns the single Published template for the given product type within the current tenant.</summary>
internal sealed record GetActiveTemplateForProductQuery(ProductType ProductType)
    : IRequest<Result<QualificationTemplateDto>>;
