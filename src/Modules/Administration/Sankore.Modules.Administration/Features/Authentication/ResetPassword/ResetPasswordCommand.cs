using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ResetPassword;

/// <summary>
/// Resets the password of an Active user using a token issued by ForgotPassword.
/// This is the second step of the forgot-password flow.
/// </summary>
public sealed record ResetPasswordCommand(
    string UserId,
    string Token,
    string NewPassword,
    string ConfirmPassword) : IRequest<Result<ResetPasswordResult>>;

public sealed record ResetPasswordResult(string Message);
