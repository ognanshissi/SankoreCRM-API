using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.CreateRole;

public sealed record CreateRoleCommand(string Name, string Label)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Role";
    public string? ResourceId => null;
}
