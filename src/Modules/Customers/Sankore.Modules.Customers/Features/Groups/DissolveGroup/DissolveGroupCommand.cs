namespace Sankore.Modules.Customers.Features.Groups.DissolveGroup;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Dissolves a group (US-M01-BE-21) — terminal and irreversible: the status
/// becomes Dissolved and every still-open membership is closed in the same
/// transaction (closed, never deleted).
/// </summary>
public sealed record DissolveGroupCommand(
    Guid GroupId,
    string Reason,
    uint? ExpectedVersion = null
) : IRequest<Result<DissolveGroupResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientGroup";
    public string? ResourceId => GroupId.ToString();
}

/// <summary><paramref name="ClosedMemberships"/> counts the memberships this call closed.</summary>
public sealed record DissolveGroupResult(
    Guid GroupId,
    string Status,
    DateTimeOffset? DissolvedAt,
    int ClosedMemberships);
