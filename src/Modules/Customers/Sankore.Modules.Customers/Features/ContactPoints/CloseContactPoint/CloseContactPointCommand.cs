using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.ContactPoints.CloseContactPoint;

/// <summary>
/// Closes a contact point by dating its <c>ValidTo</c>. There is no physical
/// deletion anywhere in this zone: a contact point that was once used to reach a
/// client is compliance evidence and stays readable as history.
/// </summary>
public sealed record CloseContactPointCommand(
    Guid ClientId,
    Guid ContactPointId
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
