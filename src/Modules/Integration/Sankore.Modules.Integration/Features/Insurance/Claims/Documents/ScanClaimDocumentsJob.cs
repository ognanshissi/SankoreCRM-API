namespace Sankore.Modules.Integration.Features.Insurance.Claims.Documents;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// ASS-09, criterion 1: runs the antivirus pass over the pieces waiting for one, then sends to the
/// insurer those it asked for.
///
/// <para>
/// <b>Two passes in one job, and the order is the obligation.</b> Nothing may be transmitted
/// before the scan has run — <c>ClaimDocument.MarkTransmitted</c> throws on a piece that is not
/// <c>Clean</c>, so the gate is on the aggregate and not only here — and a piece scanned clean by
/// a run that then failed to send is picked up by the next one. Running them as two jobs would let
/// them drift apart in time, so a complement uploaded at a counter would wait a day.
/// </para>
///
/// <para>
/// <b>Scanning is out of band and not on the upload path, deliberately.</b> An agent at a counter
/// must not wait on a daemon, and a scanner outage must not stop a dossier being assembled. The
/// trade is that an infected object sits in the store until the sweep reaches it — acceptable
/// because it lies there encrypted under this module's own key, unreferenced by any path that
/// serves bytes: <c>GET .../content</c> refuses anything but <c>Clean</c>.
/// </para>
///
/// <para>
/// <b>An infected piece loses its bytes and keeps its row.</b> The row is the fact that somebody
/// uploaded it, which is exactly what an incident review needs; the object is what must not
/// survive, and the enum's own documentation says the slice that owns the store is what removes
/// it. This is that slice.
/// </para>
///
/// <para>
/// <b>Only a piece the insurer ASKED for is sent automatically.</b> A dossier's own up-front
/// pieces travelled — or will travel — with the declaration; pushing every scan result at the
/// insurer would attach documents nobody requested to a claim under review. That is what
/// <c>RequestedByInsurer</c> is for, and it is set at upload.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type.
public sealed class ScanClaimDocumentsJob(
    IServiceScopeFactory scopeFactory,
    ILogger<ScanClaimDocumentsJob> logger)
{
    /// <summary>
    /// Pieces scanned per run. Each one is a decrypt plus a scanner round trip, so the ceiling
    /// bounds both the memory and the time one sweep can take.
    /// </summary>
    private const int ScanBatchSize = 50;

    /// <summary>Transmissions queued per run.</summary>
    private const int TransmitBatchSize = 100;

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        // SYSTEM identity, established BEFORE the DI scope: ITenantContext and ICurrentUser are
        // built from it and a Hangfire job has no HTTP context.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var store = sp.GetRequiredService<IClaimDocumentStore>();
        var scanner = sp.GetRequiredService<IClaimDocumentScanner>();
        var integration = sp.GetRequiredService<IIntegrationModule>();
        var clock = sp.GetRequiredService<TimeProvider>();

        var scanned = await ScanAsync(db, store, scanner, clock, tenantId, ct);
        var queued = await TransmitAsync(db, integration, clock, tenantId, ct);

        if (scanned.Total > 0 || queued > 0)
        {
            logger.LogInformation(
                "Claim pieces for tenant {TenantId}: {Clean} clean, {Infected} INFECTED, "
                + "{Failed} unscannable, {Queued} transmission(s) queued",
                tenantId, scanned.Clean, scanned.Infected, scanned.Failed, queued);
        }
    }

    // ── pass one: the antivirus ─────────────────────────────────────────────

    private async Task<(int Total, int Clean, int Infected, int Failed)> ScanAsync(
        IntegrationDbContext db,
        IClaimDocumentStore store,
        IClaimDocumentScanner scanner,
        TimeProvider clock,
        Guid tenantId,
        CancellationToken ct)
    {
        // Pending AND ScanFailed: the second is our own outage and is retryable, which is the
        // whole reason the enum separates it from Infected. An Infected piece is never re-scanned —
        // its bytes are gone, so there is nothing left to look at.
        //
        // IgnoreQueryFilters with an explicit tenant predicate, and AsTracking because every row
        // loaded here is loaded to be mutated: the context is NoTracking by default and RecordScan
        // on a detached entity would write nothing at all.
        var waiting = await db.ClaimDocuments
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(d => d.TenantId == tenantId
                     && (d.ScanStatus == DocumentScanStatus.Pending
                      || d.ScanStatus == DocumentScanStatus.ScanFailed))
            .OrderBy(d => d.UploadedAt)
            .Take(ScanBatchSize)
            .ToListAsync(ct);

        if (waiting.Count == 0) return (0, 0, 0, 0);

        var clean = 0;
        var infected = 0;
        var failed = 0;

        foreach (var document in waiting)
        {
            var bytes = await store.OpenAsync(tenantId, document.StorageRef, ct);

            if (bytes is null)
            {
                // The object is gone — purged, or a reference this tenant never owned. Recorded as
                // a scan failure and not as clean: a piece whose bytes cannot be read is one
                // nothing may vouch for, and leaving it Pending would have the sweep re-read a
                // missing object every night for ever.
                document.RecordScan(
                    DocumentScanStatus.ScanFailed, "The stored object could not be read.", clock);

                logger.LogWarning(
                    "Claim piece {DocumentId} could not be read from the store; recorded as "
                    + "unscannable", document.Id);

                failed++;
                continue;
            }

            ClaimScanVerdict verdict;

            try
            {
                verdict = await scanner.ScanAsync(bytes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The port's contract is to answer a verdict rather than throw, and this is the
                // belt around it: one scanner misbehaving must not abandon the rest of the batch,
                // and the piece must not be left Pending with nothing recorded.
                logger.LogError(ex, "The antivirus threw on claim piece {DocumentId}", document.Id);

                verdict = ClaimScanVerdict.Failed($"The scanner threw: {ex.GetType().Name}.");
            }

            document.RecordScan(verdict.Status, verdict.Detail, clock);

            switch (verdict.Status)
            {
                case DocumentScanStatus.Clean:
                    clean++;
                    break;

                case DocumentScanStatus.Infected:
                    infected++;

                    // The bytes go and the row stays. Logged at WARNING with the signature and
                    // NEVER the file name: an agent names a scan after the customer often enough
                    // that it is personal data, and this line is the one that gets forwarded.
                    logger.LogWarning(
                        "Claim piece {DocumentId} on dossier {ClaimId} is INFECTED ({Signature}); "
                        + "removing the stored object and keeping the row",
                        document.Id, document.ClaimId, verdict.Detail);

                    try
                    {
                        await store.DeleteAsync(tenantId, document.StorageRef, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // Swallowed: the verdict is what matters and it is about to be saved. An
                        // object that could not be deleted is a cleanup problem; losing the verdict
                        // would make the piece transmittable on the next sweep.
                        logger.LogError(ex,
                            "Could not remove the infected claim object of piece {DocumentId}",
                            document.Id);
                    }

                    break;

                default:
                    failed++;
                    break;
            }
        }

        await db.SaveChangesAsync(ct);

        return (waiting.Count, clean, infected, failed);
    }

    // ── pass two: the transmission ──────────────────────────────────────────

    private async Task<int> TransmitAsync(
        IntegrationDbContext db,
        IIntegrationModule integration,
        TimeProvider clock,
        Guid tenantId,
        CancellationToken ct)
    {
        // Clean, asked for by the insurer, not yet sent — and on a dossier the insurer has
        // acknowledged, because the payload is keyed by the EXTERNAL claim id and a dossier
        // without one has nothing to attach to. AsTracking: MarkTransmitted mutates.
        var pending = await db.ClaimDocuments
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(d => d.TenantId == tenantId
                     && d.ScanStatus == DocumentScanStatus.Clean
                     && d.RequestedByInsurer
                     && d.TransmittedAt == null)
            .OrderBy(d => d.UploadedAt)
            .Take(TransmitBatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0) return 0;

        var claimIds = pending.Select(d => d.ClaimId).Distinct().ToList();

        var dossiers = await db.Claims
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && claimIds.Contains(c.Id)
                     && c.ExternalClaimId != null)
            .Select(c => new { c.Id, c.ExternalClaimId })
            .ToListAsync(ct);

        var externalByClaim = dossiers.ToDictionary(c => c.Id, c => c.ExternalClaimId!);
        var queued = 0;

        foreach (var document in pending)
        {
            if (!externalByClaim.TryGetValue(document.ClaimId, out var externalClaimId))
            {
                // The insurer has not acknowledged the declaration yet. Left alone — not failed,
                // because nothing was attempted — so the next sweep sends it once the
                // acknowledgement lands.
                logger.LogDebug(
                    "Claim piece {DocumentId} waits on its dossier's insurer reference",
                    document.Id);
                continue;
            }

            try
            {
                var commandId = await integration.Insurance.RequestClaimDocumentAsync(
                    new ClaimDocumentPayload(
                        ExternalClaimId: externalClaimId,
                        ClaimDocumentId: document.Id,
                        StorageRef: document.StorageRef,
                        DocumentKind: document.DocumentKind,
                        FileName: document.FileName,
                        ContentType: document.ContentType,
                        SizeBytes: document.SizeBytes,
                        Sha256: document.Sha256),
                    ct);

                // The aggregate re-checks IsTransmittable and throws otherwise. Belt and braces on
                // purpose: this is the last code that runs before a customer's document leaves the
                // institution, and it must not take a second-hand word for the antivirus pass —
                // the same posture the holding-subscription consumer takes on a signature.
                document.MarkTransmitted(commandId.Value, clock);
                queued++;
            }
            catch (DomainException ex)
            {
                // No adapter, no capability, or the insurer's connection is off. The piece is
                // untouched, so tomorrow's run tries again — and it is an administrator's to fix,
                // not a retry's.
                logger.LogWarning(
                    "Claim piece {DocumentId} not queued for the insurer: {Reason}",
                    document.Id, ex.Message);
            }
        }

        await db.SaveChangesAsync(ct);

        return queued;
    }
}
