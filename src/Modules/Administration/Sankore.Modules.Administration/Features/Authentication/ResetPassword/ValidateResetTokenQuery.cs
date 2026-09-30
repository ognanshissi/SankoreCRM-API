using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ResetPassword;

/// <summary>
/// Validates a password-reset token WITHOUT consuming it.
///
/// Call it before displaying the "new password" form: otherwise the user types a password twice,
/// submits, and only then learns the link expired — with the token now burnt either way. Mirrors
/// <c>ValidateActivationTokenQuery</c>, which exists for the same reason on the activation flow.
/// </summary>
public sealed record ValidateResetTokenQuery(
    string UserId,
    string Token) : IRequest<Result>;
