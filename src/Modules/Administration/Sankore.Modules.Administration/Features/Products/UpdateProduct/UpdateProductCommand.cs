using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.UpdateProduct;

internal sealed record UpdateProductCommand(
    Guid ProductId,
    string Name,
    string? Description = null,
    string? ParametersJson = null,
    string? BusinessProductId = null,
    string? BusinessPlatformName = null,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? CustomFields = null
) : IRequest<Result>, ICommand;
