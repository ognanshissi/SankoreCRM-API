namespace Sankore.Modules.Kyc.Features.Verification.RunVerification;

using System.Globalization;
using System.Text.Json;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Modules.Kyc.Features.Approval.StartApproval;
using Sankore.Modules.Kyc.Infrastructure.Crypto;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// KYC-B-03 — runs OCR, face comparison and scoring on one file, then persists every piece of
/// evidence the run produced and lets the aggregate route the file.
///
/// <para>
/// The whole handler is organised around the three — and only three — things a biometric call can
/// mean. Confusing any two of them is the failure mode this slice exists to prevent:
/// </para>
///
/// <list type="bullet">
/// <item><b>Success</b> — evidence. Recorded, scored, routed.</item>
/// <item><b>Rejected</b> — the service worked and the <i>capture</i> is unusable. A business fact:
/// the file goes back to the agent for a better photo. Re-sending the same bytes would get the
/// same answer forever, so nothing is replayed.</item>
/// <item><b>Unavailable</b> — the service told us nothing. NOTHING is recorded as a business
/// outcome, the file stays in Verifying, and a Hangfire replay tries again later. Writing an
/// outage down as a rejection would refuse an honest client because of our own downtime.</item>
/// </list>
///
/// <para>
/// That last branch is why the unreachable path returns <c>Result.Ok</c> and not
/// <c>Result.Fail</c>: <see cref="Sankore.Shared.Infrastructure.Behaviors.TransactionBehavior{T,R}"/>
/// rolls the ambient transaction back on a failed <see cref="Result"/>, which would undo the move
/// into Verifying and leave the replay nothing to resume. The caller reads
/// <see cref="RunKycVerificationResult.Outcome"/>, not the Result, to tell the three apart.
/// </para>
/// </summary>
internal sealed class RunKycVerificationHandler(
    KycDbContext db,
    IKycDocumentStore documentStore,
    IBiometryClient biometry,
    [FromKeyedServices(KycFieldProtection.Key)] IFieldEncryptor encryptor,
    [FromKeyedServices(KycFieldProtection.Key)] IBlindIndexer indexer,
    [FromKeyedServices(nameof(KycDbContext))] IEventPublisher publisher,
    IBackgroundJobClient hangfire,
    IKycSettings settings,
    ISender sender,
    TimeProvider clock,
    ILogger<RunKycVerificationHandler> logger)
    : IRequestHandler<RunKycVerificationCommand, Result<RunKycVerificationResult>>
{
    /// <summary>
    /// Global score under which no approval circuit would ever say yes. The biometric service
    /// grades Low/Medium/High and never says "refused" — the refusal is M02's call, taken here so
    /// the threshold is visible in one place rather than inferred from a level somewhere else.
    /// </summary>
    /// <summary>
    /// Fallback only. The real value is the tenant's <c>verification-rejection-floor</c>: refusing
    /// a file is a compliance decision, and where an IMF draws that line is its own, not ours.
    /// This constant is what applies if the setting cannot be read.
    /// </summary>
    private const int DefaultRejectionFloor = 40;

    /// <summary>
    /// How many times an unreachable service is retried before a human has to look. Beyond it the
    /// file is left in Verifying and logged as an error — stuck is recoverable, wrongly rejected
    /// is a complaint from a client who did nothing wrong.
    /// </summary>
    private const int MaxReplayAttempts = 3;

    public async Task<Result<RunKycVerificationResult>> Handle(
        RunKycVerificationCommand cmd, CancellationToken ct)
    {
        // AsTracking: the context is NoTracking by default, and every status change below goes
        // through the aggregate — untracked, SaveChangesAsync would write nothing and the file
        // would silently stay where it was. IgnoreQueryFilters + explicit tenant because the
        // replay job runs outside any HTTP context.
        var file = await db.KycFiles
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId && f.TenantId == cmd.TenantId, ct);

        if (file is null)
            return Result.Fail<RunKycVerificationResult>(KycErrors.FileNotFound);

        var statusGuard = GuardStatus(file);
        if (statusGuard.IsFailure)
            return Result.Fail<RunKycVerificationResult>(statusGuard.Error!);

        // Images are fetched BEFORE the file moves to Verifying, not after. A bad reference is a
        // caller mistake, and the transition is skipped entirely rather than rolled back —
        // nothing in the aggregate, nothing in the outbox, nothing to explain afterwards.
        var document = await LoadImageAsync(cmd.TenantId, cmd.DocumentStorageRef, ct);
        if (document is null)
            return Result.Fail<RunKycVerificationResult>(RunKycVerificationErrors.ImageNotFound);

        var selfie = await LoadImageAsync(cmd.TenantId, cmd.SelfieStorageRef, ct);
        if (selfie is null)
            return Result.Fail<RunKycVerificationResult>(RunKycVerificationErrors.ImageNotFound);

        if (file.Status is KycFileStatus.Collecting or KycFileStatus.ComplementRequired)
        {
            var submitted = file.SubmitForVerification(cmd.RequestedBy, clock);
            if (submitted.IsFailure)
                return Result.Fail<RunKycVerificationResult>(submitted.Error!);
        }

        // One id for the three calls, echoed to the biometric deployment so a support request can
        // be followed across two sets of logs. Not the file id: a file is verified several times.
        var correlationId = Guid.NewGuid().ToString();

        // ── 1. Read the document ────────────────────────────────────────────
        var ocr = await biometry.ReadDocumentAsync(cmd.TenantId, document, correlationId, ct);

        if (ocr.IsUnavailable)
            return await UnreachableAsync(cmd, file, ocr.Code!, ocr.Detail, ct);

        if (ocr.IsRejected)
            return await CaptureRejectedAsync(cmd, file, ocr.Code!, ct);

        // A reading with no document number is a reading we cannot deduplicate, cannot encrypt
        // and cannot attach to anything: the aggregate itself refuses to be built without a blind
        // index. That is the definition of an unreadable document, so it is reported as one
        // rather than as a 500 on a NullReferenceException three lines down.
        var rawNumber = FindDocumentNumber(ocr.Value.Fields);
        if (string.IsNullOrWhiteSpace(rawNumber))
            return await CaptureRejectedAsync(cmd, file, BiometryCodes.DocumentUnreadable, ct);

        // ── 2. Compare the faces ────────────────────────────────────────────
        var face = await biometry.MatchFaceAsync(cmd.TenantId, document, selfie, correlationId, ct);

        if (face.IsUnavailable)
            return await UnreachableAsync(cmd, file, face.Code!, face.Detail, ct);

        if (face.IsRejected)
            return await CaptureRejectedAsync(cmd, file, face.Code!, ct);

        // ── 3. Score the whole thing ────────────────────────────────────────
        var score = await biometry.ScoreAsync(
            cmd.TenantId,
            new ScoreRequest(ocr.Value, face.Value, DeclaredFields: null, DocumentType: ocr.Value.DocumentType),
            correlationId,
            ct);

        if (score.IsUnavailable)
            return await UnreachableAsync(cmd, file, score.Code!, score.Detail, ct);

        if (score.IsRejected)
            return await CaptureRejectedAsync(cmd, file, score.Code!, ct);

        // ── 4. Persist the evidence ─────────────────────────────────────────
        PersistIdentityDocument(cmd, file, ocr.Value, rawNumber);
        PersistFaceVerification(cmd, file, face.Value);

        // Read per tenant: where an IMF draws the refusal line is its own compliance decision.
        var rejectionFloor = await settings.GetIntAsync(
            cmd.TenantId, KycSettingKeys.VerificationRejectionFloor, ct);

        if (rejectionFloor <= 0) rejectionFloor = DefaultRejectionFloor;

        var level = ToConfidenceLevel(score.Value, face.Value, rejectionFloor);

        db.KycConfidenceAssessments.Add(KycConfidenceAssessment.Create(
            tenantId: cmd.TenantId,
            kycFileId: file.Id,
            globalScore: score.Value.Score,
            level: level,
            trigger: KycConfidenceTriggers.Verification,
            clock: clock,
            breakdownJson: JsonSerializer.Serialize(score.Value.Breakdown),
            flagsJson: JsonSerializer.Serialize(score.Value.Flags),
            // A verification nobody can attribute to a model version is not evidence: the service
            // is redeployed, the thresholds move, and a decision taken last quarter has to stay
            // explainable against the model that actually took it.
            serviceVersion: score.Value.ServiceVersion));

        // ── 5. Let the aggregate route the file ─────────────────────────────
        var recorded = file.RecordVerification(score.Value.Score, level, clock);
        if (recorded.IsFailure)
            return Result.Fail<RunKycVerificationResult>(recorded.Error!);

        await PublishAsync(cmd, file, "SCORED", score.Value.Score, level, rejectionCode: null, ct);

        await db.SaveChangesAsync(ct);

        // A file that reads "in validation" must HAVE its circuit: the screen lists the rungs and
        // shows who is awaited, so creating them from a consumer would leave a window where the
        // file claims to be in validation with nothing to approve. Done after the save, and
        // idempotently, so a replayed verification does not build a second circuit.
        if (file.Status == KycFileStatus.Validating)
        {
            var started = await sender.Send(
                new StartKycApprovalCommand(cmd.TenantId, file.Id, cmd.RequestedBy), ct);

            if (started.IsFailure)
            {
                // The file is verified and scored either way; refusing here would roll that back
                // over a circuit an operator can still have created from the screen.
                logger.LogError(
                    "KYC file {KycFileId} reached Validating but its approval circuit could not be "
                    + "started: {Error}", file.Id, started.Error);
            }
        }

        logger.LogInformation(
            "KYC verification of file {KycFileId} scored {Score} ({Level}); file is now {Status}",
            file.Id, score.Value.Score, level, file.Status);

        return Result.Ok(new RunKycVerificationResult(
            file.Id, file.Status, RunKycVerificationOutcome.Scored,
            score.Value.Score, level, Code: null));
    }

    // ── Status ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Collecting and ComplementRequired are the two statuses an agent may launch a verification
    /// from. Verifying is accepted as well, and only because of the replay: an unreachable service
    /// leaves the file exactly there on purpose, so refusing it would make the replay job
    /// permanently unable to finish what it was queued for. It is NOT a fourth entry point — the
    /// file is already where this handler would have put it, so nothing is transitioned.
    /// </summary>
    private static Result GuardStatus(KycFile file) => file.Status switch
    {
        KycFileStatus.Collecting or KycFileStatus.ComplementRequired or KycFileStatus.Verifying
            => Result.Ok(),
        _ => Result.Fail(KycErrors.InvalidTransition),
    };

    // ── The two non-scored outcomes ─────────────────────────────────────────

    /// <summary>
    /// The capture is unusable. The file goes back to the agent and the run is recorded as what it
    /// was — a refusal of the photograph. Deliberately NO confidence assessment: an assessment is
    /// a statement about the customer's file, and the service made none. Writing a zero here would
    /// put a failing score in a compliance history where a blurred photo belongs.
    /// </summary>
    private async Task<Result<RunKycVerificationResult>> CaptureRejectedAsync(
        RunKycVerificationCommand cmd, KycFile file, string code, CancellationToken ct)
    {
        var moved = file.RequestComplement(clock);
        if (moved.IsFailure)
            return Result.Fail<RunKycVerificationResult>(moved.Error!);

        await PublishAsync(cmd, file, "CAPTURE_REJECTED",
            score: null, level: null, rejectionCode: code, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "KYC verification of file {KycFileId} refused the capture ({Code}); back to the agent",
            file.Id, code);

        return Result.Ok(new RunKycVerificationResult(
            file.Id, file.Status, RunKycVerificationOutcome.CaptureRejected,
            ConfidenceScore: null, ConfidenceLevel: null, Code: code));
    }

    /// <summary>
    /// We learned nothing about this customer. The file stays in Verifying, no evidence row is
    /// written, no event is published — and the attempt is queued again.
    ///
    /// <para>
    /// The move into Verifying IS saved: it is what the replay resumes from, and it is also what
    /// keeps the file out of the agent's "to complete" list while we retry. Past
    /// <see cref="MaxReplayAttempts"/> the file is left there and logged as an error: a stuck file
    /// is a ticket, a rejected one is a wronged customer.
    /// </para>
    /// </summary>
    private async Task<Result<RunKycVerificationResult>> UnreachableAsync(
        RunKycVerificationCommand cmd, KycFile file, string code, string? detail, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);

        if (cmd.Attempt >= MaxReplayAttempts)
        {
            logger.LogError(
                "Biometric service still unreachable for KYC file {KycFileId} after {Attempts} "
                + "attempts ({Code}: {Detail}); the file stays in verification for a human to pick up",
                file.Id, cmd.Attempt, code, detail);
        }
        else
        {
            // Scheduled with a delay rather than enqueued: Hangfire's storage is not in this
            // module's transaction, so an immediate job could start before the commit above is
            // visible and find the file still in its previous status. The backoff also gives a
            // service that is restarting the time to come back.
            var next = cmd.Attempt + 1;
            var delay = TimeSpan.FromMinutes(5 * cmd.Attempt);

            hangfire.Schedule<ReplayKycVerificationJob>(
                job => job.ExecuteAsync(
                    cmd.KycFileId, cmd.TenantId,
                    cmd.DocumentStorageRef, cmd.SelfieStorageRef,
                    cmd.RequestedBy, next),
                delay);

            logger.LogWarning(
                "Biometric service unreachable for KYC file {KycFileId} ({Code}: {Detail}); "
                + "attempt {Next} queued in {Delay}",
                file.Id, code, detail, next, delay);
        }

        return Result.Ok(new RunKycVerificationResult(
            file.Id, file.Status, RunKycVerificationOutcome.ServiceUnavailable,
            ConfidenceScore: null, ConfidenceLevel: null, Code: code));
    }

    // ── Evidence ────────────────────────────────────────────────────────────

    private void PersistIdentityDocument(
        RunKycVerificationCommand cmd, KycFile file, OcrReading ocr, string rawNumber)
    {
        // Canonical form of the number, computed once and fed to BOTH the ciphertext and the
        // index. The blind index would survive without it — IBlindIndexer already runs
        // SensitiveValueNormalizer.NormalizeDocumentNumber for IdentityDocument, so "CI 0123-456"
        // and "ci0123456" land on the same hash either way. The CIPHERTEXT would not: encrypt the
        // reading verbatim and two scans of one card decrypt to two different strings, so every
        // consumer that compares revealed numbers — a manual review, an export to the CBS — sees
        // two documents where there is one. Same normaliser as M01 on purpose: a second
        // implementation is a second set of rules to keep in step.
        var number = SensitiveValueNormalizer.NormalizeDocumentNumber(rawNumber);

        db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
            tenantId: cmd.TenantId,
            kycFileId: file.Id,
            docType: ocr.DocumentType,
            encryptedNumber: encryptor.Encrypt(number)!,
            numberBlindIndex: indexer.Compute(BlindIndexPurpose.IdentityDocument, number),
            clock: clock,
            issuingCountry: FindFirst(ocr.Fields, "issuing_country", "nationality", "country"),
            expiryDate: ParseDate(FindFirst(ocr.Fields, "expiry_date", "date_of_expiry", "expires_on")),
            // The number is REMOVED from both JSON blobs. They are plain jsonb columns, and
            // leaving it there would store in clear the exact value the column next to it exists
            // to encrypt — the encryption would be decorative. Same reason the MRZ is kept as its
            // parsed fields and never as its raw line: that line IS the document number, spelled
            // out, by construction.
            ocrFieldsJson: JsonSerializer.Serialize(WithoutDocumentNumber(ocr.Fields)),
            mrzDataJson: ocr.Mrz is null ? null : JsonSerializer.Serialize(new
            {
                checksumValid = ocr.Mrz.ChecksumValid,
                fields = WithoutDocumentNumber(ocr.Mrz.Fields),
            }),
            storageRef: cmd.DocumentStorageRef,
            serviceVersion: ocr.ServiceVersion,
            // Stripped on the SAME key rule as the fields themselves. A confidence left behind for
            // a removed field would name the key the number arrived under, which is the one thing
            // removing it was meant to stop saying.
            ocrFieldConfidencesJson: JsonSerializer.Serialize(WithoutDocumentNumber(ocr.FieldConfidences))));
    }

    private void PersistFaceVerification(RunKycVerificationCommand cmd, KycFile file, FaceMatch match)
    {
        // Incremented first, then read: the counter is 1-based and is the attempt number this very
        // row carries, which the unique index on (file, attempt) relies on.
        file.RecordFaceMatchAttempt(clock);

        db.KycFaceVerifications.Add(KycFaceVerification.Create(
            tenantId: cmd.TenantId,
            kycFileId: file.Id,
            attempt: file.FaceMatchAttempts,
            similarityScore: match.Similarity,
            isMatch: match.IsMatch,
            clock: clock,
            selfieStorageRef: cmd.SelfieStorageRef,
            qualityScoresJson: JsonSerializer.Serialize(new
            {
                portrait = match.PortraitQuality,
                selfie = match.SelfieQuality,
            }),
            modelVersion: match.ModelVersion));
    }

    /// <summary>
    /// Projects the service's three grades onto M02's four. The extra one is <c>Rejected</c>, and
    /// the service never produces it: refusing a file is a compliance decision, not a model
    /// output. Two facts the service DOES report trigger it — the faces are not the same person,
    /// or the global score is below the tenant's floor. Both go back to the agent rather
    /// than into the approval circuit, because no approver would say yes to either.
    /// </summary>
    private static KycConfidenceLevel ToConfidenceLevel(ConfidenceScore score, FaceMatch face, int rejectionFloor)
    {
        if (!face.IsMatch || score.Score < rejectionFloor)
            return KycConfidenceLevel.Rejected;

        return score.Level switch
        {
            BiometryConfidenceLevel.High => KycConfidenceLevel.High,
            BiometryConfidenceLevel.Medium => KycConfidenceLevel.Medium,
            _ => KycConfidenceLevel.Low,
        };
    }

    private Task PublishAsync(
        RunKycVerificationCommand cmd, KycFile file, string outcome,
        int? score, KycConfidenceLevel? level, string? rejectionCode, CancellationToken ct)
        // Through the outbox of THIS DbContext so it commits with the evidence: an event without
        // its rows would notify an agent about a verification nobody can produce, and rows without
        // their event would leave the file silently waiting.
        => publisher.PublishAsync(
            new KycVerificationCompletedEvent(
                TenantId: cmd.TenantId,
                CustomerEntityId: file.CustomerId,
                KycFileId: file.Id,
                Outcome: outcome,
                Status: file.Status.ToString(),
                ConfidenceScore: score,
                ConfidenceLevel: level?.ToString(),
                RejectionCode: rejectionCode,
                CompletedAt: clock.GetUtcNow()),
            ct);

    // ── Reading the images ──────────────────────────────────────────────────

    private async Task<BiometryImage?> LoadImageAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        await using var stream = await documentStore.OpenAsync(tenantId, storageRef, ct);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        return bytes.Length == 0 ? null : new BiometryImage(bytes, SniffContentType(bytes));
    }

    /// <summary>
    /// The store hands back bytes and not the content type it was given, so it is read off the
    /// bytes themselves. Sniffing rather than defaulting to JPEG: the store accepts PDF scans too,
    /// and announcing one as an image is how a provider ends up reporting DOCUMENT_UNREADABLE for
    /// a document that was perfectly readable.
    /// </summary>
    private static string SniffContentType(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
            return "application/pdf";

        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";

        return "image/jpeg";
    }

    // ── OCR field plumbing ──────────────────────────────────────────────────

    /// <summary>
    /// Field names are the biometric service's own and it is free to change them — "number" on one
    /// deployment, "document_number" on the next. The candidates are matched on letters and digits
    /// only so a rename between the two spellings does not silently stop producing a blind index.
    /// </summary>
    private static readonly HashSet<string> DocumentNumberKeys =
    [
        "documentnumber", "documentno", "idnumber", "number", "no", "cardnumber",
    ];

    private static bool IsDocumentNumberKey(string key) => DocumentNumberKeys.Contains(Simplify(key));

    private static string Simplify(string key)
        => new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? FindDocumentNumber(IReadOnlyDictionary<string, string> fields)
        => fields.FirstOrDefault(f => IsDocumentNumberKey(f.Key) && !string.IsNullOrWhiteSpace(f.Value)).Value;

    private static Dictionary<string, string> WithoutDocumentNumber(IReadOnlyDictionary<string, string> fields)
        => fields.Where(f => !IsDocumentNumberKey(f.Key)).ToDictionary(f => f.Key, f => f.Value);

    /// <summary>Same rule, for the confidences — one generic would hide which rule is shared.</summary>
    private static Dictionary<string, double> WithoutDocumentNumber(IReadOnlyDictionary<string, double> values)
        => values.Where(v => !IsDocumentNumberKey(v.Key)).ToDictionary(v => v.Key, v => v.Value);

    private static string? FindFirst(IReadOnlyDictionary<string, string> fields, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var simplified = Simplify(candidate);
            var hit = fields.FirstOrDefault(f => Simplify(f.Key) == simplified).Value;
            if (!string.IsNullOrWhiteSpace(hit)) return hit;
        }

        return null;
    }

    /// <summary>
    /// Day-first formats are tried before the invariant parser, which reads "02/04/1987" as the
    /// 4th of February. An unparsable date is dropped rather than guessed: a wrong expiry silently
    /// downgrades a valid file to the simplified tier later on.
    /// </summary>
    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return DateOnly.TryParseExact(
            raw.Trim(),
            ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyyMMdd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>
/// What caused a <see cref="KycConfidenceAssessment"/>. The entity's <c>Trigger</c> is a plain
/// string, so the value this slice writes is named once here rather than spelled out at the call
/// site — a typo in a literal would quietly produce an assessment history nothing can filter.
/// </summary>
internal static class KycConfidenceTriggers
{
    public const string Verification = "VERIFICATION";
}
