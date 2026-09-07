using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.ListProducts;

internal sealed record ListProductsQuery : IRequest<Result<IReadOnlyList<ProductDto>>>;