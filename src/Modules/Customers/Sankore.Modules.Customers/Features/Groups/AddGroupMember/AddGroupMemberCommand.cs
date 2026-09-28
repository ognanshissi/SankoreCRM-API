namespace Sankore.Modules.Customers.Features.Groups.AddGroupMember;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Adds a client to a group (US-M01-BE-20).
///
/// <paramref name="ExpectedVersion"/> is the group's xmin token as returned by
/// <c>GET client-groups/{groupId}</c>. It is optional so that a simple caller can
/// omit it, but any UI that displayed the membership roll should send it back:
/// without it, two operators filling the same group concurrently can both pass
/// the size check and push the group one member over its maximum.
/// </summary>
public sealed record AddGroupMemberCommand(
    Guid GroupId,
    Guid ClientId,
    GroupOfficeRole OfficeRole,
    uint? ExpectedVersion = null
) : IRequest<Result<AddGroupMemberResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientGroup";
    public string? ResourceId => GroupId.ToString();
}

/// <summary>
/// <paramref name="Activated"/> is true when this join is the one that completed
/// the activation prerequisites and moved the group from Forming to Active.
/// </summary>
public sealed record AddGroupMemberResult(
    Guid MembershipId,
    Guid GroupId,
    Guid ClientId,
    string OfficeRole,
    string GroupStatus,
    int ActiveMemberCount,
    bool Activated);
