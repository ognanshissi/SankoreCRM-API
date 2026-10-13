using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AdminResetPassword;

/// <summary>
/// Admin-initiated password reset for another user in the same tenant.
/// No reset token required — caller must hold <c>user:reset-password</c>.
/// The target user's password is expired immediately so they are forced to
/// change it on next login.
/// </summary>
internal sealed record AdminResetPasswordCommand(
    Guid TargetUserId,
    [property: SensitiveData] string NewPassword,
    [property: SensitiveData] string ConfirmPassword) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => TargetUserId.ToString();
}
