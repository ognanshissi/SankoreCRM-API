namespace Sankore.Modules.Kyc.Domain;

using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Kernel;

/// <summary>
/// A validator's verdict on one uploaded image.
///
/// <para>
/// Three values and not a boolean: <c>Pending</c> is the state of every row the moment it is
/// uploaded, and it is NOT the same thing as a refusal. A screen that cannot tell "nobody has
/// looked at this yet" from "somebody looked and said no" would show an untouched file as a
/// rejected one.
/// </para>
/// </summary>
public enum KycDocumentReviewDecision
{
    /// <summary>
    /// Predates per-document review, and no backfill can repair it.
    ///
    /// <para>
    /// Deliberately first, so it is also <c>default</c>: a row that somehow reached the database
    /// without the factory reads as "we do not know", never as "waiting for a validator".
    /// </para>
    ///
    /// <para>
    /// It exists because the registry starts EMPTY. A document uploaded before this feature left no
    /// row — only a <c>storage_ref</c> on the verification evidence — and the digest, size and
    /// content type the registry carries were never persisted anywhere, so they cannot be
    /// reconstructed. <c>ListKycDocuments</c> therefore projects those older images into the same
    /// DTO at this value, exactly as <c>KycIdentityDocumentDto.FieldConfidences</c> renders a
    /// reading taken before confidences were stored: the caller must show "unknown", not "certain".
    /// </para>
    /// </summary>
    NotReviewed,

    /// <summary>Uploaded through the registry, nobody has judged it yet.</summary>
    Pending,

    /// <summary>A validator looked at the image and accepts it as evidence.</summary>
    Accepted,

    /// <summary>A validator refuses it; <see cref="KycDocument.RefusalReason"/> says why.</summary>
    Refused
}

/// <summary>
/// The registry of images attached to a KYC file: one row per upload, carrying where the bytes are
/// and what a human decided about them.
///
/// <para>
/// This entity exists because nothing used to represent an uploaded document. The upload endpoint
/// put bytes in the encrypted store and handed an opaque <c>storageRef</c> back to the caller, who
/// passed it to <c>POST kyc-files/{id}/verify</c> — so a file's images could not be listed, and
/// there was nowhere to record a verdict on one. <see cref="KycIdentityDocument"/> is not that
/// place: it is what OCR *read*, it is created by the verification rather than by the upload, and
/// it carries no status at all.
/// </para>
///
/// <para>
/// APPEND-ONLY. A new upload of the same <see cref="Kind"/> supersedes nothing; "the current front
/// of the identity document" is the latest <see cref="UploadedAt"/> for that kind, the same idiom
/// <c>GetKycIdentityDocumentHandler</c> already uses to pick the current reading. A flag would be a
/// second source of truth for an ordering the timestamps already give.
/// </para>
///
/// <para>
/// There is no foreign key to <see cref="KycFile"/>, matching every other evidence table here:
/// <see cref="KycFileId"/> is a plain id with an index. The row is tenant-scoped through
/// <c>AggregateRoot</c> and the context's global query filter.
/// </para>
/// </summary>
public sealed class KycDocument : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid KycFileId { get; private set; }

    public KycDocumentKind Kind { get; private set; }

    /// <summary>Opaque reference into the encrypted store. Never a filesystem path.</summary>
    public string StorageRef { get; private set; } = default!;

    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }

    /// <summary>
    /// Digest of the PLAINTEXT, as the store computed it. Kept so a later check can prove the image
    /// behind a validated decision is the one that was validated.
    /// </summary>
    public string Sha256 { get; private set; } = default!;

    public Guid UploadedBy { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }

    public KycDocumentReviewDecision ReviewDecision { get; private set; }
    public Guid? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }

    /// <summary>
    /// Why the document was refused — the validator's own words, shown to the agent who has to
    /// produce a better one.
    ///
    /// <para>
    /// Same category as <c>KycApprovalStep.Comment</c>: a motive read by an operator, never a field
    /// value. It is deliberately not encrypted and not <c>[SensitiveData]</c>, which is exactly why
    /// the validator's message warns against typing a document number or an address into it.
    /// </para>
    /// </summary>
    public string? RefusalReason { get; private set; }

    /// <summary>
    /// PostgreSQL xmin — optimistic concurrency, as on <see cref="KycFile"/>.
    ///
    /// <para>
    /// Carried here even though <c>KycApprovalStep</c> has none, because decide-once on its own does
    /// not survive a race: the guard in <see cref="Review"/> is an in-memory comparison, so two
    /// validators who both load a <c>Pending</c> row both pass it and the second write wins
    /// silently, overwriting a recorded decision and its author. The token turns that into
    /// <c>KYC_CONCURRENCY_CONFLICT</c>.
    /// </para>
    /// </summary>
    public uint Version { get; private set; }

    private KycDocument() { }

    /// <summary>
    /// Records an image that has just been stored. The caller passes the values the store returned
    /// in its <c>KycStoredDocument</c>, so the row and the object cannot disagree about the
    /// reference, the size or the digest.
    ///
    /// <para>
    /// Takes those four as primitives rather than the store's own record: the domain should not need
    /// an infrastructure type to describe a document, and a test can register one without building
    /// a storage DTO.
    /// </para>
    /// </summary>
    public static KycDocument Register(
        Guid tenantId,
        Guid kycFileId,
        KycDocumentKind kind,
        string storageRef,
        string contentType,
        long sizeBytes,
        string sha256,
        Guid uploadedBy,
        TimeProvider clock)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (kycFileId == Guid.Empty) throw new DomainException("KycFileId is required.");

        if (string.IsNullOrWhiteSpace(storageRef))
            throw new DomainException("A document without a storage reference points at nothing.");

        return new KycDocument
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            Kind = kind,
            StorageRef = storageRef,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            UploadedBy = uploadedBy,
            UploadedAt = clock.GetUtcNow(),
            ReviewDecision = KycDocumentReviewDecision.Pending,
        };
    }

    /// <summary>
    /// One validator's decision on this image.
    ///
    /// <para>
    /// DECIDE-ONCE, following <c>KycApprovalStep.Decide</c>: a decision is evidence, and re-deciding
    /// would rewrite who judged what. A refused document is not edited back to accepted — the agent
    /// uploads a better image, which produces a NEW row with a fresh <c>Pending</c>, and the refusal
    /// stays on the record as the reason that upload was needed.
    /// </para>
    ///
    /// <para>
    /// The reason is required for a refusal and ignored for an acceptance: a refusal is a to-do list
    /// for somebody else, an acceptance has nothing to explain. The validator is passed in rather
    /// than taken from the command — same rule as the approval circuit, where a caller-supplied
    /// actor would be a four-eyes rule anybody could walk around.
    /// </para>
    /// </summary>
    public Result Review(
        KycDocumentReviewDecision decision, Guid reviewedBy, TimeProvider clock, string? reason = null)
    {
        // Pending and NotReviewed are states, not decisions. Accepting either here would let a
        // caller un-review a document, which is the one thing this row exists to make impossible.
        if (decision is KycDocumentReviewDecision.Pending or KycDocumentReviewDecision.NotReviewed)
            return Result.Fail(KycErrors.InvalidTransition);

        if (ReviewDecision != KycDocumentReviewDecision.Pending)
            return Result.Fail(KycErrors.DocumentAlreadyReviewed);

        if (decision == KycDocumentReviewDecision.Refused && string.IsNullOrWhiteSpace(reason))
            return Result.Fail(KycErrors.DocumentReasonRequired);

        ReviewDecision = decision;
        ReviewedBy = reviewedBy;
        ReviewedAt = clock.GetUtcNow();

        // Only a refusal keeps a reason. Storing one on an acceptance would make "why was this
        // accepted" look like a recorded answer when nobody was asked.
        RefusalReason = decision == KycDocumentReviewDecision.Refused ? reason!.Trim() : null;

        return Result.Ok();
    }

    /// <summary>True once a validator has judged this image either way.</summary>
    public bool IsReviewed => ReviewDecision != KycDocumentReviewDecision.Pending;
}
