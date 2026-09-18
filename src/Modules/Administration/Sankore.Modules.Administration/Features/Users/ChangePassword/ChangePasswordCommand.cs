using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ChangePassword;

/// <summary>
/// Allows an authenticated user to set a new password without requiring a reset token.
/// Enforces the same password-history policy as the forgot-password flow (last 12 passwords).
/// </summary>
internal sealed record ChangePasswordCommand(
    string NewPassword,
    string ConfirmPassword) : IRequest<Result>, ICommand;
