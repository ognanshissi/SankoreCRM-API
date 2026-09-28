namespace Sankore.Modules.Customers.Features.Groups.RemoveGroupMember;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Removes a client from a group (US-M01-BE-20).
///
/// "Remove" means CLOSE: the <c>group_memberships</c> row is stamped with
/// <c>LeftAt</c> and <c>LeaveReason</c> and kept forever — the joint-liability
/// history of a solidarity group is audit evidence and is never deleted.
/// </summary>
public sealed record RemoveGroupMemberCommand(
    Guid GroupId,
    Guid ClientId,
    string Reason,
    uint? ExpectedVersion = null
) : IRequest<Result<RemoveGroupMemberResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientGroup";
    public string? ResourceId => GroupId.ToString();
}

/// <summary>
/// <paramref name="BelowMinimumSize"/> is true when the departure took an Active
/// group under <c>group-min-size-&lt;type&gt;</c>. The group KEEPS its status:
/// only an alert (<c>ClientUnderMinimumGroupSizeEvent</c>) is raised, so an
/// operator decides whether to recruit or to suspend.
/// </summary>
public sealed record RemoveGroupMemberResult(
    Guid GroupId,
    Guid ClientId,
    string GroupStatus,
    int ActiveMemberCount,
    int MinimumSize,
    bool BelowMinimumSize);
