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
    string? ImpersonatorName = null,
    /// <summary>
    /// Codes of the tenant's paid modules in force (<c>BankingMicrofinance</c>, <c>Insurance</c>).
    /// The front-end shows a module's screens only when its code is here. Carried with the user
    /// and not on the public tenant context, which anyone knowing the domain can read: which
    /// modules an institution pays for is not public.
    /// </summary>
    IReadOnlyList<string>? ActiveModules = null,
    /// <summary>Modules deactivated by the tenant: their screens stay open for consultation only.</summary>
    IReadOnlyList<string>? ReadOnlyModules = null,
    /// <summary>
    /// The session works on the tenant's TEST TWIN (sandbox lot 2): data and configuration are the
    /// sandbox's, the team is production's. The front shows its banner while this is true.
    /// </summary>
    bool TestMode = false);
