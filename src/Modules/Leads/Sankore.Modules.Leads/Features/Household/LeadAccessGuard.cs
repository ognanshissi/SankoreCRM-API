namespace Sankore.Modules.Leads.Features.Household;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Whether the caller may work this particular lead (BNF-B-03: "pour l'agent assigné ou un
/// superviseur").
///
/// <para>
/// The agency perimeter already decides which leads a user can SEE; this is the narrower question
/// of who may change a household — the person responsible for the relationship, or someone above
/// them. A permission alone cannot express it, because every commercial agent holds the same
/// permission and they must not edit each other's leads.
/// </para>
///
/// <para>
/// A service rather than a branch in three handlers: the rule is worth testing once, and "the
/// caller is a supervisor" is a cross-module read (<c>GetTeamAgentIdsAsync</c>) that has to
/// suppress the ambient transaction like every other one in this module.
/// </para>
/// </summary>
/// <remarks>
/// <b>Public for a mechanical reason, not a design one</b> — the same one <c>HoldingSyncService</c>
/// gives in M01: <c>LeadsModuleFacade</c> is public because it implements the module's contract,
/// and a public type cannot take an internal constructor dependency. It stays in
/// <c>Features.Household</c>, nothing outside this assembly references it, and TEL-B-06's
/// click-to-call reaches it only through the facade.
/// </remarks>
public sealed class LeadAccessGuard(ICurrentUser currentUser, IAdministrationModule administration)
{
    public const string Forbidden = "LEAD_NOT_YOURS";

    /// <summary>
    /// Roles that reach every lead of their tenant without an ownership check. Deliberately short:
    /// <c>BranchManager</c> is NOT here, because a branch manager's reach is their branch and that
    /// is exactly what <c>GetTeamAgentIdsAsync</c> computes — adding them would replace a real
    /// perimeter with a blanket one.
    /// </summary>
    private static readonly string[] BypassRoles =
    [
        Roles.System.Code,
        Roles.Administrator.Code,
        Roles.SalesManager.Code,
    ];

    /// <summary>
    /// Ok when the caller is the lead's current assignee, its owner, the agent who collected it,
    /// or the supervisor of whoever holds it.
    /// </summary>
    /// <remarks>
    /// The SYSTEM role and the two management roles pass unconditionally. <c>System</c> because
    /// that is what <c>SystemCurrentUser</c> carries and a Hangfire job has no team;
    /// <c>Administrator</c> and <c>SalesManager</c> because a supervisor whose team is empty — a
    /// manager in a one-agency tenant, say — would otherwise be locked out of every lead by a
    /// contract that answers "nobody reports to you".
    ///
    /// <para>
    /// A lead held by NOBODY — no assignment, no owner — is refused rather than open to all. It
    /// cannot happen through the household endpoints, which require
    /// <c>Lead.IsHouseholdEditable</c> and therefore one of the two, but this guard is about to be
    /// reused by Lot 6's enrichment and "unheld means everyone" is the wrong default to leave
    /// lying around.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether the user holds one of the roles that reach every lead of the tenant — the same
    /// list this guard applies, for a caller that asks a different question with the same answer
    /// (a WhatsApp reassignment reaching beyond a supervisor's own team).
    /// </summary>
    public static bool HasBypassRole(ICurrentUser user)
        => user.Roles.Intersect(BypassRoles, StringComparer.OrdinalIgnoreCase).Any();

    public async Task<Result> EnsureCanWorkAsync(Lead lead, CancellationToken ct)
    {
        if (currentUser.Roles.Intersect(BypassRoles, StringComparer.OrdinalIgnoreCase).Any())
            return Result.Ok();

        var me = currentUser.Id;

        if (me == Guid.Empty)
            return Result.Fail(Forbidden);

        if (lead.CurrentAssignedId == me || lead.OwnerId == me || lead.AgentCollectedLeadId == me)
            return Result.Ok();

        var holder = lead.CurrentAssignedId ?? lead.OwnerId;
        if (holder is null)
            return Result.Fail(Forbidden);

        IReadOnlyList<Guid> team;
        try
        {
            // Suppressed: M12 reads through its own DbContext and a second connection enlisting in
            // TransactionBehavior's scope promotes it to a distributed transaction Npgsql cannot do.
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            team = await administration.GetTeamAgentIdsAsync(lead.TenantId, me, ct);
            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fails CLOSED, unlike the best-effort lookups on the capture path. This one decides
            // whether somebody may edit another person's lead, and an outage is not a reason to
            // widen that — the caller retries.
            return Result.Fail(Forbidden);
        }

        return team.Contains(holder.Value) ? Result.Ok() : Result.Fail(Forbidden);
    }
}
