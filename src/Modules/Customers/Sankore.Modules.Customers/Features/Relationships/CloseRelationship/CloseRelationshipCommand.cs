using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.Relationships.CloseRelationship;

/// <summary>
/// Closes a relationship by dating its <c>ValidTo</c> and recording why. Nothing is
/// ever physically deleted: a guarantor who backed a loan must stay visible long
/// after the link ended.
/// </summary>
public sealed record CloseRelationshipCommand(
    Guid ClientId,
    Guid RelationshipId,
    string? Reason
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
