namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A pair of clients the nightly detection job believes to be the same person.
/// The pair is stored canonically with <see cref="ClientAId"/> &lt; <see cref="ClientBId"/> so
/// that the unique index on (tenant, A, B) catches the mirrored pair too.
/// The fingerprints are the hashes of the compared fields at detection time: when one of them
/// changes, a pair a reviewer had rejected legitimately comes back for review.
/// </summary>
public sealed class DuplicateCandidate
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientAId { get; private set; }
    public Guid ClientBId { get; private set; }
    public int Score { get; private set; }

    /// <summary>JSON array of the <c>MatchSignal</c>s behind the score, for the review screen.</summary>
    public string ReasonsJson { get; private set; } = default!;

    public DuplicateCandidateStatus Status { get; private set; }
    public string FingerprintA { get; private set; } = default!;
    public string FingerprintB { get; private set; } = default!;
    public DateTimeOffset DetectedAt { get; private set; }
    public Guid? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }

    private DuplicateCandidate() { } // EF Core

    public static DuplicateCandidate Detect(
        Guid tenantId,
        Guid clientAId,
        Guid clientBId,
        int score,
        string reasonsJson,
        string fingerprintA,
        string fingerprintB,
        DateTimeOffset detectedAt)
    {
        if (clientAId == Guid.Empty || clientBId == Guid.Empty)
            throw new DomainException("Both clients are required.", "DuplicateCandidate.Clients.Required");
        if (clientAId == clientBId)
            throw new DomainException("A client cannot duplicate itself.", "DuplicateCandidate.Self.Forbidden");

        // Canonical ordering so the pair is stored once, whichever way round it was detected.
        if (clientAId.CompareTo(clientBId) > 0)
        {
            (clientAId, clientBId) = (clientBId, clientAId);
            (fingerprintA, fingerprintB) = (fingerprintB, fingerprintA);
        }

        return new DuplicateCandidate
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientAId = clientAId,
            ClientBId = clientBId,
            Score = Math.Clamp(score, 0, 100),
            ReasonsJson = string.IsNullOrWhiteSpace(reasonsJson) ? "[]" : reasonsJson,
            Status = DuplicateCandidateStatus.ToReview,
            FingerprintA = fingerprintA ?? string.Empty,
            FingerprintB = fingerprintB ?? string.Empty,
            DetectedAt = detectedAt,
        };
    }

    public void Reject(Guid actor, DateTimeOffset at)
    {
        Status = DuplicateCandidateStatus.Rejected;
        ReviewedBy = actor;
        ReviewedAt = at;
    }

    public void MarkMerged(Guid actor, DateTimeOffset at)
    {
        Status = DuplicateCandidateStatus.Merged;
        ReviewedBy = actor;
        ReviewedAt = at;
    }

    /// <summary>
    /// Re-scores an existing pair. When a fingerprint moved, the previous review no longer applies
    /// and the pair goes back to <see cref="DuplicateCandidateStatus.ToReview"/>.
    /// </summary>
    public void Refresh(int score, string reasonsJson, string fingerprintA, string fingerprintB, DateTimeOffset at)
    {
        var fieldsChanged = !string.Equals(FingerprintA, fingerprintA, StringComparison.Ordinal)
                            || !string.Equals(FingerprintB, fingerprintB, StringComparison.Ordinal);

        Score = Math.Clamp(score, 0, 100);
        ReasonsJson = string.IsNullOrWhiteSpace(reasonsJson) ? "[]" : reasonsJson;
        FingerprintA = fingerprintA ?? string.Empty;
        FingerprintB = fingerprintB ?? string.Empty;
        DetectedAt = at;

        if (fieldsChanged && Status == DuplicateCandidateStatus.Rejected)
        {
            Status = DuplicateCandidateStatus.ToReview;
            ReviewedBy = null;
            ReviewedAt = null;
        }
    }
}
