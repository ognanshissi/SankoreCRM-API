namespace Sankore.Modules.Leads.Features.QualificationTemplates.ResolveQualificationTemplate;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Resolves the best qualification template using the same 3-level fallback as before:
/// 1. Template with matching ProductCode (product-specific)
/// 2. Template with matching ProductCategory and no ProductCode (category-level)
/// 3. Template with no ProductCategory and no ProductCode (generic)
/// </summary>
/// <param name="ProductCode">
/// A product code, or any product LABEL. Kept as the only required input so every existing caller
/// is unaffected.
/// </param>
/// <param name="ProductLineId">
/// A resolved M12 product line, when the caller has one (BNF-B-01). <b>Preferred over
/// <paramref name="ProductCode"/>:</b> a lead's product arrives as free text, so the code the
/// label happens to spell may match no catalogue row — and then levels 1 and 2 both miss and every
/// lead falls through to the generic template. A line id resolves to the AUTHORITATIVE code and
/// category in one cached read.
/// </param>
internal sealed record ResolveQualificationTemplateQuery(
    string ProductCode,
    Guid? ProductLineId = null)
    : IRequest<Result<QualificationTemplateDto>>;
