namespace Sankore.Modules.Kyc.Features.Retention;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// A client was anonymised (retention, or a person's request — data-purge lot 2): the KYC evidence
/// goes with them. Before, nothing in this module listened, and the ID card scans, selfies and
/// document numbers of an anonymised client stayed readable forever.
///
/// <para>
/// M01 only anonymises once <c>IsRetentionClearedAsync</c> answered yes, so no file here is still
/// being worked or suspended. The files themselves stay — status, tier, dates, decisions are the
/// compliance history — emptied of what identifies the person: documents (bytes then row), identity
/// documents, selfies and face payloads, corrected values.
/// </para>
///
/// <para>
/// Bytes first, row second, as the recording purge does: a row emptied whose object survived is a
/// file nobody will look for again. An object that resists keeps its row as it was, and the
/// redelivered message — or the next anonymisation of the same client — retries it. Idempotent:
/// an emptied row has nothing left to delete.
/// </para>
/// </summary>
public sealed class ClientAnonymizedKycConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<ClientAnonymizedKycConsumer> logger)
    : IConsumer<ClientAnonymizedEvent>
{
    public async Task Consume(ConsumeContext<ClientAnonymizedEvent> context)
    {
        var evt = context.Message;
        if (evt.TenantId == Guid.Empty || evt.ClientId == Guid.Empty) return;

        // Before the scope: ITenantContext and ICurrentUser are built from this ambient context.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        await EraseAsync(
            sp.GetRequiredService<KycDbContext>(), sp.GetService<IKycDocumentStore>(), logger,
            evt.TenantId, evt.ClientId, context.CancellationToken);
    }

    internal static async Task EraseAsync(
        KycDbContext db, IKycDocumentStore? store, ILogger logger, Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var fileIds = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.CustomerId == clientId)
            .Select(f => f.Id)
            .ToListAsync(ct);
        if (fileIds.Count == 0) return;

        async Task<bool> DeleteObjectAsync(string? storageRef)
        {
            if (string.IsNullOrEmpty(storageRef) || store is null) return true;
            try
            {
                await store.DeleteAsync(tenantId, storageRef, ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "KYC erasure: an object of client {ClientId} (tenant {TenantId}) could not be "
                    + "deleted; its row is kept for a retry.", clientId, tenantId);
                return false;
            }
        }

        var documents = await db.KycDocuments.IgnoreQueryFilters().AsTracking()
            .Where(d => fileIds.Contains(d.KycFileId) && d.StorageRef != "")
            .ToListAsync(ct);
        foreach (var document in documents)
            if (await DeleteObjectAsync(document.StorageRef)) document.Erase();

        var identities = await db.KycIdentityDocuments.IgnoreQueryFilters().AsTracking()
            .Where(d => fileIds.Contains(d.KycFileId))
            .ToListAsync(ct);
        foreach (var identity in identities)
            if (await DeleteObjectAsync(identity.StorageRef)) identity.Erase();

        var faces = await db.KycFaceVerifications.IgnoreQueryFilters().AsTracking()
            .Where(f => fileIds.Contains(f.KycFileId))
            .ToListAsync(ct);
        foreach (var face in faces)
            if (await DeleteObjectAsync(face.SelfieStorageRef)) face.Erase();

        var corrections = await db.KycFieldCorrections.IgnoreQueryFilters().AsTracking()
            .Where(c => fileIds.Contains(c.KycFileId))
            .ToListAsync(ct);
        foreach (var correction in corrections) correction.Erase();

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Client {ClientId} of tenant {TenantId} anonymised: KYC evidence of {Files} file(s) erased "
            + "({Documents} document(s), {Identities} identity document(s), {Faces} selfie(s)).",
            clientId, tenantId, fileIds.Count, documents.Count, identities.Count, faces.Count);
    }
}
