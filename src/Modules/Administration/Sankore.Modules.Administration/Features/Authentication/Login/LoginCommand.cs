using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.Login;

/// <summary>
/// TenantId is required because users share email space across tenants.
/// The login endpoint is anonymous, so there is no JWT to extract it from.
/// </summary>
/// <param name="IpAddress">
/// Filled by the endpoint from the connection, not by the caller: a client-supplied address in
/// a security log is worth nothing. Null when a proxy strips it.
/// </param>
/// <param name="UserAgent">Raw header, parsed into browser/platform when the login is recorded.</param>
public sealed record LoginCommand(
    string Email,
    [property: SensitiveData] string Password,
    string? IpAddress = null,
    string? UserAgent = null
) : IRequest<Result<LoginResult>>, ICommand;

public sealed record LoginResult(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid UserId);
