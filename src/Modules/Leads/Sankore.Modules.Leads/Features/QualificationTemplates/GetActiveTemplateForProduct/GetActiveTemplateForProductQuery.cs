namespace Sankore.Modules.Leads.Features.QualificationTemplates.GetActiveTemplateForProduct;

using MediatR;
using Sankore.Modules.Leads.Features.QualificationTemplates;
using Sankore.Shared.Kernel;

/// <summary>Returns the single Published template for the given product category within the current tenant.</summary>
internal sealed record GetActiveTemplateForProductQuery(string ProductCategory)
    : IRequest<Result<QualificationTemplateDto>>;
