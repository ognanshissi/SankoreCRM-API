namespace Sankore.Modules.Administration.Features.Products;

using Sankore.Modules.Administration.Domain;

internal sealed record ProductDto(
    Guid Id,
    string Name,
    string Code,
    ProductCategory Category,
    string? Description,
    string? ParametersJson,
    bool IsActive,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo);
