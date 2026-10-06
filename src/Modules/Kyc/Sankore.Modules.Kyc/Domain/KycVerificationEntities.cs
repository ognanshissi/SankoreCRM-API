namespace Sankore.Modules.Kyc.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// The identity document attached to a KYC file.
///
/// The number is NEVER stored in clear: <see cref="EncryptedNumber"/> holds the AES-GCM payload
/// and <see cref="NumberBlindIndex"/> an HMAC of the normalised value, which is what duplicate
/// detection searches on. Looking for a duplicate must never decrypt anything — the same rule M01
/// applies to phones and identity documents.
/// </summary>
public sealed class KycIdentityDocument : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid KycFileId { get; private set; }

    public string DocType { get; private set; } = default!;
    public string EncryptedNumber { get; private set; } = default!;
    public string NumberBlindIndex { get; private set; } = default!;
    public string? IssuingCountry { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    /// <summary>Raw OCR output, as JSON. Field VALUES are sensitive; the audit records names only.</summary>
    public string? OcrFieldsJson { get; private set; }

    /// <summary>
    /// The service's confidence per OCR field, 0..1, keyed like <see cref="OcrFieldsJson"/>.
    ///
    /// <para>
    /// Stored because KYC-F-02 is "the fields read, <em>with their confidence</em>": an agent
    /// correcting a reading needs to know which values the machine was unsure of, and a screen that
    /// shows every field as equally certain sends them re-typing the ones that were already right.
    /// The service reports it on the same call that produced the fields, so not keeping it meant
    /// the information existed exactly once and was thrown away.
    /// </para>
    /// </summary>
    public string? OcrFieldConfidencesJson { get; private set; }

    public string? MrzDataJson { get; private set; }

    /// <summary>Opaque reference into the encrypted document store. Never a filesystem path.</summary>
    public string? StorageRef { get; private set; }

    /// <summary>
    /// The biometric service's OWN answer to <c>/v1/ocr</c>, encrypted (AES-GCM, same key as
    /// <see cref="EncryptedNumber"/>).
    ///
    /// <para>
    /// It exists because <c>/v1/score</c> is stateless and takes that answer back verbatim: the
    /// projections beside it drop the per-field source, the anomalies, the image quality and the
    /// MRZ checks, all of which the score is computed from — so without this a re-score after a
    /// field correction could only hand the scorer a different document, or nothing at all.
    /// </para>
    ///
    /// <para>
    /// ENCRYPTED and not a plain jsonb column, unlike every other blob here: this payload contains
    /// the document number, in <c>fields</c> and again in <c>mrz</c>. That is precisely why
    /// <see cref="OcrFieldsJson"/> has the number stripped out of it — storing the service's answer
    /// in clear would undo, one column over, what <see cref="EncryptedNumber"/> exists to do.
    /// </para>
    /// </summary>
    public string? EncryptedOcrPayload { get; private set; }

    public string? ServiceVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private KycIdentityDocument() { }

    public static KycIdentityDocument Create(
        Guid tenantId, Guid kycFileId, string docType,
        string encryptedNumber, string numberBlindIndex, TimeProvider clock,
        string? issuingCountry = null, DateOnly? expiryDate = null,
        string? ocrFieldsJson = null, string? mrzDataJson = null,
        string? storageRef = null, string? serviceVersion = null,
        string? ocrFieldConfidencesJson = null, string? encryptedOcrPayload = null)
    {
        if (string.IsNullOrWhiteSpace(numberBlindIndex))
            throw new DomainException("A document without a blind index could never be deduplicated.");

        return new KycIdentityDocument
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            DocType = docType,
            EncryptedNumber = encryptedNumber,
            NumberBlindIndex = numberBlindIndex,
            IssuingCountry = issuingCountry,
            ExpiryDate = expiryDate,
            OcrFieldsJson = ocrFieldsJson,
            OcrFieldConfidencesJson = ocrFieldConfidencesJson,
            MrzDataJson = mrzDataJson,
            StorageRef = storageRef,
            ServiceVersion = serviceVersion,
            EncryptedOcrPayload = encryptedOcrPayload,
            CreatedAt = clock.GetUtcNow(),
        };
    }

    /// <summary>True when the document is past its expiry on the given day — drives downgrade.</summary>
    public bool IsExpiredOn(DateOnly day) => ExpiryDate is { } expiry && expiry < day;

    public void ReplaceOcrFields(string? ocrFieldsJson) => OcrFieldsJson = ocrFieldsJson;
}

/// <summary>
/// One face-comparison attempt. Kept per attempt, not per file: the number of attempts changes
/// which approval circuit applies, so each one is evidence.
/// </summary>
public sealed class KycFaceVerification : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid KycFileId { get; private set; }

    public string? SelfieStorageRef { get; private set; }
    public double SimilarityScore { get; private set; }
    public bool IsMatch { get; private set; }
    public string? QualityScoresJson { get; private set; }
    public string? ModelVersion { get; private set; }

    /// <summary>
    /// The service's OWN answer to <c>/v1/face-match</c>, encrypted. Same reason as
    /// <see cref="KycIdentityDocument.EncryptedOcrPayload"/>: the scorer is handed it back
    /// verbatim, and <see cref="QualityScoresJson"/> keeps only two of its numbers.
    ///
    /// <para>
    /// It carries no document number, but it is encrypted all the same — it describes a named
    /// person's face, and a quality or detection score is biometric data about them.
    /// </para>
    /// </summary>
    public string? EncryptedFacePayload { get; private set; }

    /// <summary>1-based attempt number on this file.</summary>
    public int Attempt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private KycFaceVerification() { }

    public static KycFaceVerification Create(
        Guid tenantId, Guid kycFileId, int attempt, double similarityScore, bool isMatch,
        TimeProvider clock, string? selfieStorageRef = null,
        string? qualityScoresJson = null, string? modelVersion = null,
        string? encryptedFacePayload = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            Attempt = attempt,
            SimilarityScore = similarityScore,
            IsMatch = isMatch,
            SelfieStorageRef = selfieStorageRef,
            QualityScoresJson = qualityScoresJson,
            ModelVersion = modelVersion,
            EncryptedFacePayload = encryptedFacePayload,
            CreatedAt = clock.GetUtcNow(),
        };
}

/// <summary>
/// A confidence score as the service returned it. Appended, never updated: a correction produces a
/// new assessment so the history shows what the agent changed and what it changed the score to.
/// </summary>
public sealed class KycConfidenceAssessment : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid KycFileId { get; private set; }

    public int GlobalScore { get; private set; }
    public KycConfidenceLevel Level { get; private set; }
    public string? BreakdownJson { get; private set; }
    public string? FlagsJson { get; private set; }
    public string? ServiceVersion { get; private set; }

    /// <summary>What caused this assessment — "VERIFICATION" or "FIELD_CORRECTION".</summary>
    public string Trigger { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private KycConfidenceAssessment() { }

    public static KycConfidenceAssessment Create(
        Guid tenantId, Guid kycFileId, int globalScore, KycConfidenceLevel level,
        string trigger, TimeProvider clock,
        string? breakdownJson = null, string? flagsJson = null, string? serviceVersion = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            GlobalScore = Math.Clamp(globalScore, 0, 100),
            Level = level,
            Trigger = trigger,
            BreakdownJson = breakdownJson,
            FlagsJson = flagsJson,
            ServiceVersion = serviceVersion,
            CreatedAt = clock.GetUtcNow(),
        };
}

/// <summary>
/// An agent's correction of a machine-read field.
///
/// Both values are encrypted: an OCR field can hold a name, a date of birth or a document number,
/// and a correction table in clear would be the one place the whole point of encrypting the
/// document is undone. The audit trail records the field NAME and the author — never the values.
/// </summary>
public sealed class KycFieldCorrection : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid KycFileId { get; private set; }

    public string FieldName { get; private set; } = default!;

    /// <summary>Where the corrected value came from: <c>OCR</c> or <c>MRZ</c>.</summary>
    public string Source { get; private set; } = default!;

    public string? EncryptedPreviousValue { get; private set; }
    public string EncryptedNewValue { get; private set; } = default!;

    public Guid CorrectedBy { get; private set; }
    public DateTimeOffset CorrectedAt { get; private set; }

    private KycFieldCorrection() { }

    public static KycFieldCorrection Create(
        Guid tenantId, Guid kycFileId, string fieldName, string source,
        string? encryptedPreviousValue, string encryptedNewValue,
        Guid correctedBy, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new DomainException("A correction without a field name proves nothing.");

        return new KycFieldCorrection
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            FieldName = fieldName.Trim(),
            Source = source,
            EncryptedPreviousValue = encryptedPreviousValue,
            EncryptedNewValue = encryptedNewValue,
            CorrectedBy = correctedBy,
            CorrectedAt = clock.GetUtcNow(),
        };
    }
}
