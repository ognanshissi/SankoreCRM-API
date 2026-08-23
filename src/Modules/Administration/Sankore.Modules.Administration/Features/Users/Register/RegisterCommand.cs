using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Register;

public sealed record RegisterCommand(
    string Email,
    [property: SensitiveData] string Password,
    [property: SensitiveData] string ConfirmPassword,
    string FirstName,
    string LastName
) : IRequest<Result<RegisterResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => null; // ID not yet assigned at dispatch time
}

public sealed record RegisterResult(Guid UserId);
