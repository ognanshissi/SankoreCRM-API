using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.AccountActivation;

public sealed record AccountActivationCommand(
    string UserId,
    [property: SensitiveData] string Token,
    [property: SensitiveData] string NewPassword,
    [property: SensitiveData] string ConfirmPassword) : IRequest<Result<AccountActivationResult>>, ICommand;


public sealed record AccountActivationResult(bool Success, string Message);