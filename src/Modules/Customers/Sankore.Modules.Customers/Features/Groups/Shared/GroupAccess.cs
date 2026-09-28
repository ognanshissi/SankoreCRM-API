namespace Sankore.Modules.Customers.Features.Groups.Shared;

using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Single implementation of the agency-perimeter rule for groups.
///
/// A group outside the caller's perimeter is reported as
/// <c>GROUP_NOT_FOUND</c> (404), never <c>AGENCY_OUT_OF_SCOPE</c> (403): a 403
/// would confirm that a group with that id exists somewhere in the tenant, which
/// is itself information the caller is not entitled to.
///
/// <see cref="IAgencyScopedRequest"/> — and therefore
/// <c>AgencyAuthorizationBehavior</c> — can only be used when the target agency
/// is part of the request payload. That is true of <c>CreateGroupCommand</c>
/// only; every other slice addresses an existing group whose agency is known
/// after the row is read, hence this explicit check inside the handlers.
/// </summary>
internal static class GroupAccess
{
    internal static Task<bool> CanAccessAsync(
        IAgencyScopeProvider scope,
        Guid tenantId,
        Guid userId,
        ClientGroup group,
        CancellationToken ct)
        => scope.CanAccessAgencyAsync(tenantId, userId, group.AgencyId, ct);
}
