using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Users.Features.Register;

public sealed record RegisterCommand(
    Guid TenantId,
    Guid AgencyId,
    string Email,
    string Password,
    string ConfirmPassword,
    string FullName
) : IRequest<Result<RegisterResult>>, ICommand;

public sealed record RegisterResult(Guid UserId);