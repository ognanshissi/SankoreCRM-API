using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.BulkAssign;

/// <summary>Gives one role to a selection of users.</summary>
public sealed record BulkAssignRoleCommand(IReadOnlyList<Guid> UserIds, Guid RoleId)
    : IRequest<Result<BulkAssignResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Role";
    public string? ResourceId => RoleId.ToString();
}
