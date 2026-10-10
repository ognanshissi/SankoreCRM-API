namespace Sankore.Modules.Integration.Features.Sync;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — the TARGETED synchronisation of one customer (INT-20, criterion 4). Enqueued by
/// <see cref="SyncWebhookEndpoints"/> once a webhook's signature has been verified, on the same
/// <c>integration-sync</c> queue as the scheduled sweep.
///
/// <para>
/// <b>Targeted, never a full sweep.</b> A core banking system that emits a webhook per customer
/// event would otherwise turn one notification into a walk of every customer of the tenant, and a
/// burst of notifications into a self-inflicted denial of service against the bank that sent them.
/// </para>
///
/// <para>
/// <b>Enqueued, never run inside the request.</b> The webhook endpoint is public and
/// unauthenticated; doing the projection in its handler would let anyone holding the signing
/// secret hold an HTTP worker for the length of a CBS call, and would make the answer the sender
/// sees depend on the bank's own availability.
/// </para>
///
/// <para>
/// <b>It does not touch the stream cursor.</b> Refreshing one customer says nothing about how far
/// the stream has been read, and advancing the cursor here would declare a window synchronised
/// that nobody looked at — criterion 2's failure mode, reached from the other side.
/// </para>
/// </summary>
[Queue(IntegrationSyncJob.QueueName)]
public sealed class SyncCustomerJob(IServiceScopeFactory scopeFactory)
{
    /// <param name="externalId">
    /// The customer's identifier <b>in the external system</b>, taken from the webhook body. It is
    /// a lookup key into <c>integration_reference</c> and nothing else: see
    /// <see cref="RunAsync"/> for why no data from that body is ever stored.
    /// </param>
    public async Task ExecuteAsync(Guid tenantId, Guid connectionId, string externalId)
    {
        // Set BEFORE the scope is created — ITenantContext is built from it. Same rule as every
        // job in this repository.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<SyncCustomerJob>>();

        await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<ICbsSnapshotProjector>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            connectionId,
            externalId,
            CancellationToken.None);
    }

    /// <summary>
    /// Resolves the external identifier against <c>integration_reference</c> and projects that one
    /// customer. Returns whether anything was projected.
    ///
    /// <para>
    /// <b>Criterion 3 applies here too, and here it is also an authorisation check.</b> The
    /// external id arrives in a webhook body: it is remote input. Resolving it through
    /// <c>integration_reference</c> scoped to this connection AND this tenant is what stops a
    /// signed webhook from one IMF naming another IMF's customer — the reference table is the only
    /// record that this tenant's CRM customer and that external identity are the same person, and
    /// it was written by us. An unknown id is not an error: it is a customer the CRM has never
    /// exchanged with this system, and there is nothing to refresh.
    /// </para>
    ///
    /// <para>
    /// Nothing from the webhook body is stored. The body named a customer; the data comes from the
    /// projection, which reads the external system itself. A webhook that could write values would
    /// be an unauthenticated write path into the snapshot.
    /// </para>
    /// </summary>
    internal static async Task<bool> RunAsync(
        IntegrationDbContext db,
        ICbsSnapshotProjector projector,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid connectionId,
        string externalId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        // IgnoreQueryFilters paired with an explicit tenant predicate: a job has no ambient tenant
        // and the one being served is the argument.
        var reference = await db.References
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId
                  && r.ConnectionId == connectionId
                  && r.EntityType == IntegrationEntityTypes.Customer
                  && r.ExternalId == externalId, ct);

        if (reference is null)
        {
            logger.LogInformation(
                "Targeted sync for tenant {TenantId} connection {ConnectionId}: the external "
                + "identifier is not referenced; nothing to synchronise.",
                tenantId, connectionId);

            return false;
        }

        await projector.ProjectAsync(tenantId, connectionId, reference.CrmId, ct);
        reference.MarkSynced(clock);
        await db.SaveChangesAsync(ct);

        return true;
    }
}
