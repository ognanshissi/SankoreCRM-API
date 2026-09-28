namespace Sankore.Modules.Customers.Features.Groups.SuspendGroup;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Suspends a group (US-M01-BE-21) — a reversible freeze, unlike a dissolution:
/// memberships stay open and the group can be brought back later.
/// The motive is mandatory because it is what the audit trail records.
/// </summary>
public sealed record SuspendGroupCommand(
    Guid GroupId,
    string Reason,
    uint? ExpectedVersion = null
) : IRequest<Result<SuspendGroupResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientGroup";
    public string? ResourceId => GroupId.ToString();
}

public sealed record SuspendGroupResult(Guid GroupId, string Status, int ActiveMemberCount);
