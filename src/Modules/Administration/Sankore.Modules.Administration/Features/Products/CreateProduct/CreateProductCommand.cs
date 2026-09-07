using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.CreateProduct;

internal sealed record CreateProductCommand(
    string Name,
    string Code,
    string? Description
) : IRequest<Result<Guid>>, ICommand;