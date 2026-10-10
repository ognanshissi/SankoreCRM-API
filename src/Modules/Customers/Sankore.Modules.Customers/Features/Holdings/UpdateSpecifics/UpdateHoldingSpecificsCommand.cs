namespace Sankore.Modules.Customers.Features.Holdings.UpdateSpecifics;

using MediatR;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Replaces a holding's terms (BNF-B-14). Only before the client signs: past that, what the client
/// agreed to is no longer what the record says.
/// </summary>
public sealed record UpdateHoldingSpecificsCommand(
    Guid HoldingId,
    HoldingSpecificsDto Specifics
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductHolding";
    public string? ResourceId => HoldingId.ToString();
}
