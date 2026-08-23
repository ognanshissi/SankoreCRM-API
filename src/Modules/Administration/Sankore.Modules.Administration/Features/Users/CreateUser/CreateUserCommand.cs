using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.CreateUser;

/// <summary>
/// Admin-initiated user creation. Creates the account in PendingActivation status
/// and triggers an activation email. No password is set at creation time.
/// </summary>
public sealed record CreateUserCommand(
    Guid AgencyId,
    Guid RoleId,
    string FullName,
    string Email,
    string DefaultLanguage,
    List<string> SpokenLanguages,
    List<string> Specialties,
    Guid CallerUserId
) : IRequest<Result<CreateUserResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => null; // ID not yet assigned at dispatch time
}

public sealed record CreateUserResult(Guid UserId);
