namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// Which of a tenant's connections the inbound sweep has anything to do with.
///
/// <para>
/// One definition, read by both jobs: the orchestrator asks whether the list is empty before
/// fanning a tenant out, and the per-tenant job walks it. Two predicates would be two answers to
/// "does this tenant run a batch connection", and the one in the orchestrator is the one that
/// decides whether the other ever runs.
/// </para>
///
/// <para>
/// <b>The filter is on the settings OBJECT, in memory, not on the kind.</b>
/// <c>ConnectionSettings</c> is jsonb behind a value converter, so no SQL predicate can ask
/// whether a row carries <see cref="BatchCapableSettings"/>; the alternative would be a hardcoded
/// list of <c>IntegrationKind</c> values, which would be a second copy of a fact the settings
/// hierarchy already states — and wrong the first time an Amplitude installation moves from
/// Legacy to Up. A tenant has a handful of connections, so the read is cheap.
/// </para>
/// </summary>
internal static class InboundBatchTargets
{
    /// <summary>
    /// The tenant's active file-based connections.
    ///
    /// <para>
    /// <b>Active only.</b> A deactivated connection is one the tenant stopped using, and polling
    /// its directory would keep applying acknowledgements from a system nobody has authorised us
    /// to talk to any more. The consequence is deliberate and worth knowing: deactivating a
    /// connection that still has <c>Batched</c> commands silences their overdue alerts, which is
    /// a decision an administrator takes explicitly.
    /// </para>
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every background
    /// path in this repository does: a job runs outside any HTTP request and the ambient tenant is
    /// not necessarily the one being swept.
    /// </para>
    /// </summary>
    public static async Task<List<InboundBatchTarget>> ListAsync(
        IntegrationDbContext db, Guid tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var connections = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.IsActive)
            .ToListAsync(ct);

        return [.. connections
            .Where(c => c.Settings is BatchCapableSettings)
            .Select(c => new InboundBatchTarget(
                c,
                // A connection that deposits but declares no inbound directory still owes the
                // overdue alerts of criterion 3 — the files it sent exist and nothing will ever
                // answer them. Only the polling half needs a directory.
                CanPoll: !string.IsNullOrWhiteSpace(((BatchCapableSettings)c.Settings!).InboundDirectory)))];
    }
}

/// <summary>One connection the inbound sweep serves, and whether it has a directory to poll.</summary>
internal sealed record InboundBatchTarget(IntegrationConnection Connection, bool CanPoll);
