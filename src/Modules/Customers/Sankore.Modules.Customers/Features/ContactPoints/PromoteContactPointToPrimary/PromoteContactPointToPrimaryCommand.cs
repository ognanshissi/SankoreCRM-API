using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.ContactPoints.PromoteContactPointToPrimary;

/// <summary>
/// Makes one contact point the primary of its type. Exactly one active primary per
/// type is allowed, so the previous primary of that type is demoted in the same
/// transaction.
/// </summary>
public sealed record PromoteContactPointToPrimaryCommand(
    Guid ClientId,
    Guid ContactPointId
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
