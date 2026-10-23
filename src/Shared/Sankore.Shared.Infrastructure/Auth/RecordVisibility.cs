namespace Sankore.Shared.Infrastructure.Auth;

using Sankore.Shared.Kernel;

/// <summary>
/// Whether a caller sees EVERY lead, client or KYC file their perimeter reaches, or only the ones
/// assigned to them (product owner, 2026-10-23: an Agent or a CommercialAgent consults only what is
/// assigned to them).
///
/// <para>
/// Expressed as the ABSENCE of a « read:all » permission rather than as a list of roles, so a
/// tenant decides it from its Roles screen. The consequence is deliberate: a custom role nobody
/// granted the permission to is restricted too.
/// </para>
///
/// <para>
/// What « assigned » means is each module's — the lead's holder, the client's advisor, the file's
/// responsible agent, each falling back on whoever created the record when nobody holds it — and
/// lives next to that module's entity. This class only answers « does the restriction apply ».
/// </para>
/// </summary>
public static class RecordVisibility
{
    /// <summary>
    /// True when the restriction does NOT apply: no authenticated user (a consumer outside any
    /// scope), the System role (every Hangfire job and scoped consumer) or Administrator (a token
    /// older than the permission still carries the role), or the permission itself.
    /// </summary>
    public static bool SeesAll(ICurrentUser user, PermissionItem readAll)
    {
        if (!user.IsAuthenticated)
            return true;

        foreach (var role in user.Roles)
        {
            if (string.Equals(role, Roles.System.Code, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, Roles.Administrator.Code, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return user.Permissions.Contains(readAll.Code, StringComparer.Ordinal);
    }
}
