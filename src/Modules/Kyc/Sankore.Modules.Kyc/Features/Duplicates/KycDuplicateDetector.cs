namespace Sankore.Modules.Kyc.Features.Duplicates;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// KYC-B-04 — finds another OPEN file of the same tenant whose identity document carries the same
/// number, and flags the file under check.
///
/// <para>
/// A service, not a MediatR handler: it runs inside the verification command's transaction, as one
/// step of a larger act. Sending a nested command would give it its own transaction scope and its
/// own audit entry for something the agent never asked for.
/// </para>
///
/// <para>
/// It NEVER decrypts. The search is an equality lookup on <c>NumberBlindIndex</c>, which is what
/// <c>ix_kyc_identity_documents_blind_index</c> covers — a scan that decrypted every stored number
/// to compare it would both be unindexable and defeat the encryption it is searching through.
/// </para>
/// </summary>
internal sealed class KycDuplicateDetector(
    KycDbContext db,
    // Keyed to this module: the same document number must not produce the same index in M01 and
    // in M02, or a leak of one module's index would be a lookup table for the other's.
    [FromKeyedServices(KycFieldProtection.Key)] IBlindIndexer indexer,
    TimeProvider clock)
{
    /// <summary>
    /// The blind index of a raw document number as an agent or an OCR pass produced it.
    ///
    /// <para>
    /// The number is normalised FIRST — trimmed, upper-cased, separators dropped — because the
    /// index is an exact-match lookup: "CI 001-234", "ci001234" and " CI001234 " are one document
    /// and must land on one index, or the same card typed two ways would not match itself and the
    /// detection would quietly find nothing. <c>HmacBlindIndexer</c> applies the identical rule for
    /// <see cref="BlindIndexPurpose.IdentityDocument"/>; calling it here too is idempotent and
    /// keeps the normalisation visible at the only place a caller can get it wrong.
    /// </para>
    /// </summary>
    public string ComputeBlindIndex(string documentNumber)
        => indexer.Compute(
            BlindIndexPurpose.IdentityDocument,
            SensitiveValueNormalizer.NormalizeDocumentNumber(documentNumber));

    /// <summary>
    /// Convenience for a caller holding the clear number it has just read off a document.
    /// </summary>
    public Task<Guid?> DetectForNumberAsync(
        Guid tenantId, Guid kycFileId, string documentNumber, CancellationToken ct)
        => DetectAsync(tenantId, kycFileId, ComputeBlindIndex(documentNumber), ct);

    /// <summary>
    /// Returns the id of the colliding file, or null when the document is unique among the
    /// tenant's open files. On a hit the file under check is flagged, which raises its vigilance
    /// to High — so the approval circuit gains the compliance officer.
    /// </summary>
    /// <remarks>
    /// Does not call SaveChanges: the flag must commit with whatever the caller is doing, and a
    /// file flagged by a verification that then fails would be a suspicion nobody raised.
    /// </remarks>
    public async Task<Guid?> DetectAsync(
        Guid tenantId, Guid kycFileId, string numberBlindIndex, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(numberBlindIndex))
            return null;

        // IgnoreQueryFilters + an explicit tenant predicate: this runs from consumers and jobs
        // where the ambient tenant is not the one being processed. The predicate is NOT optional —
        // dropping it would make a document of another tenant a "duplicate", which is both a false
        // positive and a cross-tenant leak of the fact that the document exists there.
        var open = db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId
                     && f.Status != KycFileStatus.Rejected
                     && f.Status != KycFileStatus.Suspended);

        var collidingFileId = await db.KycIdentityDocuments
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId
                     && d.NumberBlindIndex == numberBlindIndex
                     // A file is never its own duplicate: the document being checked is already
                     // stored by the time detection runs.
                     && d.KycFileId != kycFileId)
            .Join(open, d => d.KycFileId, f => f.Id, (_, f) => (Guid?)f.Id)
            .FirstOrDefaultAsync(ct);

        if (collidingFileId is null)
            return null;

        var file = await db.KycFiles
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.Id == kycFileId, ct);

        // A hit on a file that does not exist is still reported: the caller asked whether the
        // number is already known, and it is. Inventing a flag on nothing would be the only wrong
        // answer here.
        file?.FlagDuplicateSuspected(clock);

        return collidingFileId;
    }
}
