namespace Sankore.Modules.Kyc.Features.Documents.ListDocuments;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Kernel;

/// <summary>
/// Lists a file's images from the registry, and projects the older ones that never had a registry
/// row into the same shape.
///
/// <para>
/// There is no backfill, deliberately. An image uploaded before this feature left only a
/// <c>storage_ref</c> on the verification evidence; its content type, size and plaintext digest
/// were computed inside the store and persisted nowhere, and the object itself is AES-GCM
/// ciphertext, so a migration could neither recover nor recompute them. Writing four NULL columns
/// and an approximate timestamp would have manufactured rows that look reviewable and are not.
/// Projecting them at read time says the same thing, writes nothing, and cannot be wrong later.
/// </para>
///
/// <para>
/// Those projected entries carry <c>NotReviewed</c> and a null id — the same choice
/// <c>KycIdentityDocumentDto.FieldConfidences</c> makes for a reading taken before confidences were
/// stored. They are evidence of what was collected, not work waiting for a validator, and marking
/// them <c>Accepted</c> would forge a decision nobody took in a module built on "a decided step is
/// evidence".
/// </para>
/// </summary>
internal sealed class ListKycDocumentsHandler(KycDbContext db)
    : IRequestHandler<ListKycDocumentsQuery, Result<KycDocumentListDto>>
{
    public async Task<Result<KycDocumentListDto>> Handle(
        ListKycDocumentsQuery query, CancellationToken ct)
    {
        var file = await db.KycFiles
            .Where(f => f.Id == query.KycFileId)
            .Select(f => new { f.Id, f.Status })
            .FirstOrDefaultAsync(ct);

        if (file is null)
            return Result.Fail<KycDocumentListDto>(KycErrors.FileNotFound);

        var registered = await db.KycDocuments
            .Where(d => d.KycFileId == query.KycFileId)
            .ToListAsync(ct);

        // The storage refs a verification recorded, used for two things: to tell which images have
        // an OCR reading, and to recover the ones that never got a registry row.
        var readImages = await db.KycIdentityDocuments
            .Where(d => d.KycFileId == query.KycFileId && d.StorageRef != null)
            .Select(d => new { StorageRef = d.StorageRef!, d.CreatedAt })
            .ToListAsync(ct);

        var selfies = await db.KycFaceVerifications
            .Where(v => v.KycFileId == query.KycFileId && v.SelfieStorageRef != null)
            .Select(v => new { StorageRef = v.SelfieStorageRef!, v.CreatedAt })
            .ToListAsync(ct);

        var ocrRefs = readImages.Select(r => r.StorageRef).ToHashSet(StringComparer.Ordinal);
        var knownRefs = registered.Select(d => d.StorageRef).ToHashSet(StringComparer.Ordinal);

        var entries = registered
            .Select(d => new KycDocumentDto(
                Id: d.Id,
                Kind: d.Kind.ToString(),
                StorageRef: d.StorageRef,
                ContentType: d.ContentType,
                SizeBytes: d.SizeBytes,
                Sha256: d.Sha256,
                UploadedBy: d.UploadedBy,
                UploadedAt: d.UploadedAt,
                ReviewDecision: d.ReviewDecision.ToString(),
                ReviewedBy: d.ReviewedBy,
                ReviewedAt: d.ReviewedAt,
                RefusalReason: d.RefusalReason,
                // Filled in below, once every entry of a kind is known.
                IsCurrentForKind: false,
                HasOcrReading: ocrRefs.Contains(d.StorageRef)))
            .ToList();

        // The legacy images, in the two places a ref was ever recorded. A ref already in the
        // registry is skipped: an uploaded-then-verified document appears in both, and the registry
        // row is the richer one.
        //
        // CreatedAt is the VERIFICATION time, not the upload time — the closest thing that exists.
        // It is only ever used to order these entries among themselves.
        entries.AddRange(
            readImages
                .Where(r => !knownRefs.Contains(r.StorageRef))
                .Select(r => Legacy(r.StorageRef, KycDocumentKind.IdentityDocumentFront, r.CreatedAt, hasOcr: true)));

        entries.AddRange(
            selfies
                .Where(s => !knownRefs.Contains(s.StorageRef))
                .Select(s => Legacy(s.StorageRef, KycDocumentKind.Selfie, s.CreatedAt, hasOcr: false)));

        // Newest first within a kind, so "the current one" is the first of its group.
        var ordered = entries
            .GroupBy(e => e.Kind, StringComparer.Ordinal)
            .SelectMany(g => g
                .OrderByDescending(e => e.UploadedAt)
                .Select((e, index) => e with { IsCurrentForKind = index == 0 }))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenByDescending(e => e.UploadedAt)
            .ToList();

        var current = ordered.Where(e => e.IsCurrentForKind).ToList();

        return Result.Ok(new KycDocumentListDto(
            KycFileId: file.Id,
            FileStatus: file.Status.ToString(),
            // "At least one" matters: an empty file has not had every document accepted, it has had
            // none, and a screen that reads `all` on an empty list would offer to validate nothing.
            AllCurrentDocumentsAccepted: current.Count > 0
                && current.TrueForAll(e => e.ReviewDecision == nameof(KycDocumentReviewDecision.Accepted)),
            AnyCurrentDocumentNotReviewed: current.Exists(
                e => e.ReviewDecision == nameof(KycDocumentReviewDecision.NotReviewed)),
            Documents: ordered));
    }

    /// <summary>An image that exists, was collected, and has no registry row to decide on.</summary>
    private static KycDocumentDto Legacy(
        string storageRef, KycDocumentKind kind, DateTimeOffset recordedAt, bool hasOcr) =>
        new(Id: null,
            Kind: kind.ToString(),
            StorageRef: storageRef,
            ContentType: null,
            SizeBytes: null,
            Sha256: null,
            UploadedBy: null,
            UploadedAt: recordedAt,
            ReviewDecision: nameof(KycDocumentReviewDecision.NotReviewed),
            ReviewedBy: null,
            ReviewedAt: null,
            RefusalReason: null,
            IsCurrentForKind: false,
            HasOcrReading: hasOcr);
}
