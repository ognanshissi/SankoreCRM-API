using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.LinkProductToCbs;

internal sealed record LinkProductToCbsCommand(
    Guid ProductId,
    string BusinessProductId,
    string BusinessPlatformName
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Product";
    public string? ResourceId  => ProductId.ToString();
}
