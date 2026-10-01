namespace Sankore.Modules.Kyc.Features.Files.ListKycFiles;

using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Which rungs of the approval ladder a set of roles may sign — used ONLY to answer "does this file
/// await me", on a dashboard.
///
/// <para>
/// <b>It is not an authorization check and must never become one.</b> Whether a given approver may
/// actually sign a given rung is the authorization pipeline's answer: the decision endpoint requires
/// <c>kyc:approve</c>, and M12 grants it for a period and a perimeter through
/// <c>PermissionAttribution</c> — including a branch manager's temporary delegation while she is
/// away. <c>DecideKycApprovalHandler</c> deliberately refuses to re-implement that, which is why
/// <c>KycErrors.ApprovalStepNotYours</c> is raised nowhere.
/// </para>
///
/// <para>
/// So this mapping is a HINT, by the owner's explicit choice: roles, not attributions. The
/// consequence is worth stating plainly — a stand-in who holds a delegation but not the role will
/// not see the file highlighted, and will still be able to sign it from the file itself. A badge
/// that under-counts is a nuisance; a badge that decided who may approve would be a second,
/// divergent implementation of the rule that matters.
/// </para>
/// </summary>
internal static class KycApproverLevels
{
    /// <summary>
    /// Ranks the roles can sign. Empty means "no rung", which makes every <c>awaitingMe</c> false
    /// rather than true — a role nobody mapped must not light up the whole dashboard.
    /// </summary>
    internal static IReadOnlyList<int> RanksFor(IEnumerable<string> roles)
    {
        var ranks = new SortedSet<int>();

        foreach (var role in roles ?? [])
        {
            // The two roles that hold every permission also answer for every rung: a file waiting
            // on any level is waiting on them.
            if (Is(role, Roles.System) || Is(role, Roles.Administrator))
            {
                ranks.Add((int)KycApprovalLevel.Agent);
                ranks.Add((int)KycApprovalLevel.BranchManager);
                ranks.Add((int)KycApprovalLevel.ComplianceOfficer);
                continue;
            }

            if (Is(role, Roles.Agent) || Is(role, Roles.CommercialAgent))
                ranks.Add((int)KycApprovalLevel.Agent);

            if (Is(role, Roles.BranchManager))
                ranks.Add((int)KycApprovalLevel.BranchManager);

            // The compliance rung. RegulationManager is the role that carries it in this tenant's
            // vocabulary; no separate "ComplianceOfficer" role exists, and inventing one here would
            // put a role name in two places.
            if (Is(role, Roles.RegulationManager))
                ranks.Add((int)KycApprovalLevel.ComplianceOfficer);
        }

        return [.. ranks];
    }

    /// <summary>
    /// Compares against <see cref="RoleItem.Code"/>, never <c>Name</c>: <c>Name</c> is the French
    /// label ("Compte Agent") and a JWT role claim carries the code. Ordinal-ignore-case because the
    /// claim comes from a token and a casing difference would silently empty every badge.
    /// </summary>
    private static bool Is(string role, RoleItem known)
        => string.Equals(role, known.Code, StringComparison.OrdinalIgnoreCase);
}
