using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ResetPassword;

/// <summary>
/// Admin-initiated password reset for a given user.
/// The last 12 password hashes are checked — reuse is rejected with
/// error code PASSWORD_RECENTLY_USED (US-M12-USER-001, Scenario 2).
/// </summary>
public sealed record ResetPasswordCommand(
    Guid UserId,
    [property: SensitiveData] string NewPassword
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}
