using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.GetProduct;

internal sealed record GetProductQuery(Guid ProductId) : IRequest<Result<ProductDto>>;