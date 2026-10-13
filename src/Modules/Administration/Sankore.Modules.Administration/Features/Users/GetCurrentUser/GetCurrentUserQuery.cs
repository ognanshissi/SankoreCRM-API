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
    string DefaultLanguage,
    /// <summary>L'administrateur qui agit sous cette identité, ou null hors usurpation.</summary>
    Guid? ImpersonatorId = null,
    string? ImpersonatorName = null);
