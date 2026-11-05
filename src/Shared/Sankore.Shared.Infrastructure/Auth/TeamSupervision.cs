using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// Whether the caller supervises a team, and how far. Read off permissions rather than role names
/// (2026-11-05, product owner): the lists of names scattered across the handlers left out any role an
/// institution created. System and Administrator pass by role, as in <see cref="RecordVisibility"/>:
/// a background identity carries the role and no permission.
/// </summary>
public static class TeamSupervision
{
    public static bool Supervises(ICurrentUser user) => Holds(user.Roles, user.Permissions, Permissions.CanSuperviseTeam);

    /// <summary>Every agency of the tenant, not only the caller's and those below it.</summary>
    public static bool CoversAllAgencies(ICurrentUser user) => Holds(user.Roles, user.Permissions, Permissions.CanCoverAllAgencies);

    public static bool Holds(IEnumerable<string> roles, IEnumerable<string>? permissions, PermissionItem permission) =>
        roles.Any(r => string.Equals(r, Roles.System.Code, StringComparison.Ordinal)
                    || string.Equals(r, Roles.Administrator.Code, StringComparison.Ordinal))
        || (permissions?.Contains(permission.Code, StringComparer.Ordinal) ?? false);
}
