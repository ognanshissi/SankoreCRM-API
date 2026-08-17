using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Login;

/// <summary>
/// TenantId is required because users share email space across tenants.
/// The login endpoint is anonymous, so there is no JWT to extract it from.
/// </summary>
public sealed record LoginCommand(
    Guid TenantId,
    string Email,
    string Password
) : IRequest<Result<LoginResult>>, ICommand;

public sealed record LoginResult(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    Guid TenantId);
