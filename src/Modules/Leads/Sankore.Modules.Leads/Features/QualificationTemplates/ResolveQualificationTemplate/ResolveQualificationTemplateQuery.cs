namespace Sankore.Modules.Leads.Features.QualificationTemplates.ResolveQualificationTemplate;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Resolves the best qualification template for a product code using 3-level fallback:
/// 1. Template with matching ProductCode (product-specific)
/// 2. Template with matching ProductCategory and no ProductCode (category-level)
/// 3. Template with no ProductCategory and no ProductCode (generic)
/// </summary>
internal sealed record ResolveQualificationTemplateQuery(string ProductCode)
    : IRequest<Result<QualificationTemplateDto>>;
