using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.DeleteProduct;

internal sealed record DeleteProductCommand(Guid ProductId) : IRequest<Result>, ICommand;