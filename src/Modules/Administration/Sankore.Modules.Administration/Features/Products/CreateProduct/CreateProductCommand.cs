using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.CreateProduct;

internal sealed record CreateProductCommand(
    string Name,
    string Code,
    ProductCategory Category,
    string? Description = null,
    string? ParametersJson = null,
    DateOnly? EffectiveFrom = null
) : IRequest<Result<Guid>>, ICommand;
