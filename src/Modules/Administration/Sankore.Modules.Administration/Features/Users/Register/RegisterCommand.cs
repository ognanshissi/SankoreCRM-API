using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Register;

public sealed record RegisterCommand(
    Guid TenantId,
    Guid AgencyId,
    string Email,
    string Password,
    string ConfirmPassword,
    string FirstName,
    string LastName,
    string Role
) : IRequest<Result<RegisterResult>>, ICommand;

public sealed record RegisterResult(Guid UserId);