using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.UpdateProduct;

internal sealed record UpdateProductCommand(
    Guid ProductId,
    string Name,
    string? Description
) : IRequest<Result>, ICommand;