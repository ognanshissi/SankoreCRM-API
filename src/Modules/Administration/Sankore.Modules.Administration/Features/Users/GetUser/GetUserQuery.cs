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
    string AccountType,
    List<string> Roles,
    /// <summary>Who this user reports to, or null at the top of the line.</summary>
    Guid? ReportsToUserId,
    /// <summary>Resolved for display so the caller does not need a second round-trip.</summary>
    string? ReportsToFullName);
