using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.DeleteProduct;

/// <summary>
/// Retires a product from the catalogue (sets EffectiveTo + IsActive=false).
/// Existing contracts referencing this product are NOT affected.
/// </summary>
internal sealed record DeleteProductCommand(
    Guid ProductId,
    DateOnly? EffectiveTo = null
) : IRequest<Result>, ICommand;
