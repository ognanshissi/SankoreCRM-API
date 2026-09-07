namespace Sankore.Modules.Administration.Features.Products;

internal sealed record ProductDto(
    Guid Id,
    string Name,
    string Code,
    string? Description);