namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// One extraction line, applied — INT-25's criterion 4: the daily extractions feed the read
/// models, <b>with the same logic as INT-21</b>.
///
/// <para>
/// <b>Which is why this class writes no snapshot.</b> It names a customer and calls
/// <see cref="ICbsSnapshotProjector.ProjectAsync"/>, exactly as <c>SyncCustomerJob</c> does for a
/// webhook. Everything about WHAT a snapshot contains — which ports are read, how a CBS code
/// becomes a CRM code, how the totals are added up, when a KYC divergence is reported — lives
/// behind that one method, and a second writer of <c>cbs_customer_snapshot</c> would be a second
/// definition of the read model that drifts the first time INT-21 gains a column.
/// </para>
///
/// <para>
/// <b>The extraction's own figures are NOT read.</b> A line may carry any number of further
/// columns and they are ignored. That is deliberate and it is also a real limit of a batch
/// connection, documented rather than worked around: a file deposited on an SFTP server is
/// unauthenticated input, and letting it write balances into the read model would be a write path
/// into a customer's financial position that nothing signed. The projector reads the external
/// system itself — which, for a CBS that exposes no port at all, means an extraction can refresh
/// nothing. See the registration's remarks.
/// </para>
///
/// <para>
/// <b>The external identifier is resolved through <c>integration_reference</c>, and that is an
/// authorisation check as much as a translation.</b> Same reasoning as <c>SyncCustomerJob</c>: the
/// reference table, scoped to this tenant AND this connection, is the only record that our
/// customer and that external identity are the same person — and it was written by us. An
/// unknown identifier is not an error, it is a customer this connection has never exchanged with.
/// </para>
/// </summary>
internal static class ExtractionApplier
{
    /// <summary>
    /// Projects the one customer a record names. Returns the line report when it names none.
    /// </summary>
    public static async Task<InboundLineReport?> ApplyAsync(
        IntegrationDbContext db,
        ICbsSnapshotProjector projector,
        TimeProvider clock,
        Guid tenantId,
        Guid connectionId,
        string fileName,
        InboundBatchRecord record,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(record);

        var externalCustomerId = record.Field(InboundBatchFileFormat.Extraction.ExternalCustomerId);

        if (externalCustomerId is null)
            return new InboundLineReport(
                fileName, record.FileLine, InboundBatchCodes.LineMalformed,
                "The record names no external customer identifier.");

        // IgnoreQueryFilters paired with explicit tenant AND connection predicates — see the type
        // remarks: this lookup is what stops one IMF's file from naming another IMF's customer.
        var reference = await db.References
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId
                  && r.ConnectionId == connectionId
                  && r.EntityType == IntegrationEntityTypes.Customer
                  && r.ExternalId == externalCustomerId, ct);

        if (reference is null)
            return new InboundLineReport(
                fileName, record.FileLine, InboundBatchCodes.CustomerNotReferenced,
                $"No {IntegrationEntityTypes.Customer} reference on this connection for "
                + $"external identifier {externalCustomerId}.");

        await projector.ProjectAsync(tenantId, connectionId, reference.CrmId, ct);

        // The extraction did refresh this entity, so the reference says when — the same line
        // SyncCustomerJob writes after the same call. It is what makes "last synchronised" mean
        // the same thing whether the figures arrived by webhook, by sweep or by file.
        reference.MarkSynced(clock);
        await db.SaveChangesAsync(ct);

        return null;
    }
}
