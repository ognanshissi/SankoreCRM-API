namespace Sankore.Modules.Integration.Features.Telephony;

using System.Transactions;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Leads.PublicApi;

/// <summary>
/// "May this agent act on that record" — asked of the module that owns it (TEL-B-09).
///
/// <para>
/// <b>Why this exists at all.</b> Attaching a call writes into a record of another module: the
/// call then appears in that client's or that lead's history, which <c>GetCallsForEntityAsync</c>
/// projects with no perimeter of its own precisely because the record's own screen already
/// decided who may see it. Without a check here, an entity id is just a Guid in a request body —
/// and an agent of one branch could file their conversation into another branch's client file,
/// or pull another branch's calls into a client of their own and read back who spoke to whom and
/// for how long. That is the shape of defect <c>DesignationSubjectValidator</c> was written for
/// in M01, where "any non-empty Guid was stored" fed a real cross-tenant leak.
/// </para>
///
/// <para>
/// <b>It asks rather than deciding.</b> The perimeter of a client is M01's agency scope; the
/// right to work a lead is M13's assignee-or-supervisor rule. Neither is a comparison this module
/// could make, and a copy of either here would be a second implementation nobody updates.
/// </para>
/// </summary>
internal sealed class TelephonyRecordAccess(
    ICustomersModule customers,
    ILeadsModule leads,
    ILogger<TelephonyRecordAccess> logger)
{
    /// <summary>
    /// <b>Fails CLOSED.</b> This is an authorization question, so the posture is
    /// <c>LeadAccessGuard</c>'s and not the capture path's: an outage refuses the attachment and
    /// the agent retries, where swallowing it would let a perimeter check that never happened read
    /// as a pass. The repo's own table of the three postures puts the access guard in exactly this
    /// column.
    /// </summary>
    internal async Task<bool> CanActOnAsync(
        Guid tenantId, string entityType, Guid entityId, Guid actorUserId, CancellationToken ct)
    {
        if (!TelEntityTypes.IsKnown(entityType) || entityId == Guid.Empty) return false;

        try
        {
            // Suppressed, like every cross-module read in a command: TransactionBehavior holds a
            // System.Transactions scope and the other module reads through its own DbContext, so a
            // second connection enlisting would promote the ambient transaction to a distributed
            // one — which Npgsql does not support.
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            return entityType == TelEntityTypes.Customer
                ? await customers.CanActorAccessClientAsync(tenantId, entityId, actorUserId, ct)
                : await leads.CanActorWorkLeadAsync(tenantId, entityId, actorUserId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Could not establish whether user {ActorUserId} may act on {EntityType} "
                  + "{EntityId} in tenant {TenantId}; refused.",
                actorUserId, entityType, entityId, tenantId);

            return false;
        }
    }
}
