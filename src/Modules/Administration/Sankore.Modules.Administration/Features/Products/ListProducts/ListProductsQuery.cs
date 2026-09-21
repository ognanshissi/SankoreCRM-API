using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.ListProducts;

internal sealed record ListProductsQuery(
    bool? ActiveOnly = null
) : IRequest<Result<IReadOnlyList<ProductDto>>>;
