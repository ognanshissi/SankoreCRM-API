namespace Sankore.Modules.Customers.Features.Groups.AssignOfficeRole;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Assigns an office role inside a group (US-M01-BE-20).
///
/// A group has at most one President, one Treasurer and one Secretary at any
/// point in time: giving a role to a new member demotes the previous holder back
/// to <see cref="GroupOfficeRole.Member"/> in the same transaction.
/// </summary>
public sealed record AssignOfficeRoleCommand(
    Guid GroupId,
    Guid ClientId,
    GroupOfficeRole OfficeRole,
    uint? ExpectedVersion = null
) : IRequest<Result<AssignOfficeRoleResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientGroup";
    public string? ResourceId => GroupId.ToString();
}

/// <summary>
/// <paramref name="PreviousHolderClientId"/> is the member demoted to Member by
/// this call, null when the role was vacant. <paramref name="Activated"/> is true
/// when filling this role completed the activation prerequisites.
/// </summary>
public sealed record AssignOfficeRoleResult(
    Guid GroupId,
    Guid ClientId,
    string OfficeRole,
    string GroupStatus,
    Guid? PreviousHolderClientId,
    bool Activated);
