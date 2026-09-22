namespace Sankore.Modules.Administration.Features.Users.GetCurrentUser;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetCurrentUserQuery : IRequest<Result<CurrentUserDto>>;

public sealed record CurrentUserDto(
    Guid Id,
    Guid TenantId,
    string Email,
    string FullName,
    Guid? AgencyId,
    bool IsSuperUser,
    string Status,
    string AccountType,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string DefaultLanguage);
