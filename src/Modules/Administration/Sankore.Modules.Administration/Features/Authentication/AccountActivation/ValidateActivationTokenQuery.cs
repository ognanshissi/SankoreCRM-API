using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.AccountActivation;

/// <summary>
/// Validates an activation token without consuming it.
/// Call this before displaying the "set password" form so the UI can show
/// a friendly error if the link is expired or already used.
/// </summary>
public sealed record ValidateActivationTokenQuery(
    string UserId,
    string Token) : IRequest<Result>;
