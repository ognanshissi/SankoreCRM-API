using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.UpdateUser;

public sealed record UpdateUserCommand(
    Guid UserId,
    string? FullName,
    Guid? AgencyId,
    List<string>? SpokenLanguages,
    List<string>? Specialties,
    bool? EnableNotifications
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}
