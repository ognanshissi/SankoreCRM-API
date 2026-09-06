using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUser;

public sealed record GetUserQuery(Guid UserId) : IRequest<Result<UserDto>>;

public sealed record UserDto(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    Guid? AgencyId,
    string? AgencyName,
    bool MfaEnabled,
    DateTimeOffset PasswordExpiresAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? DeactivatedAt,
    List<string> SpokenLanguages,
    List<string> Specialties,
    bool IsAvailable,
    bool EnableNotifications,
    string AccountType);
