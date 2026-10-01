namespace Sankore.Modules.Kyc.Features.Corrections.CorrectField;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Modules.Kyc.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// Records an agent's correction of a machine-read field and re-scores the file.
///
/// <para>
/// Re-scoring WITHOUT re-running OCR is the acceptance criterion, not an optimisation: the OCR
/// pass is what got the field wrong, so running it again would overwrite the agent's correction
/// with the same misreading. Only <see cref="IBiometryClient.ScoreAsync"/> is called, on the
/// corrected field set — the scorer is stateless and keeps no session between our calls.
/// </para>
///
/// <para>
/// <see cref="IBiometryClient"/> is injected directly rather than reached through the verification
/// slice: that slice owns a full OCR + face-match + score run, which is precisely what must not
/// happen here.
/// </para>
/// </summary>
internal sealed class CorrectKycFieldHandler(
    KycDbContext db,
    TimeProvider clock,
    IBiometryClient biometry,
    // Keyed: a KYC document number and an M01 client's phone must not be decryptable with the
    // same key. See KycFieldProtection for why a second AddFieldProtection call cannot be used.
    [FromKeyedServices(KycFieldProtection.Key)] IFieldEncryptor encryptor)
    : IRequestHandler<CorrectKycFieldCommand, Result<CorrectKycFieldResult>>
{
    public async Task<Result<CorrectKycFieldResult>> Handle(
        CorrectKycFieldCommand cmd, CancellationToken ct)
    {
        if (!KycCorrectionSources.IsKnown(cmd.Source))
            return Result.Fail<CorrectKycFieldResult>(CorrectionErrors.SourceInvalid);

        // The global query filter scopes both reads to the caller's tenant, so a file of another
        // tenant is simply absent and the caller is told NOT_FOUND — the existence of a KYC file
        // must not leak across tenants.
        var file = await db.KycFiles
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId, ct);

        if (file is null)
            return Result.Fail<CorrectKycFieldResult>(KycErrors.FileNotFound);

        var document = await db.KycIdentityDocuments
            .AsTracking()
            .Where(d => d.KycFileId == cmd.KycFileId)
            // Newest first: a file can carry a re-captured document, and the correction applies to
            // the reading currently in force, not to the superseded one kept as evidence.
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (document is null)
            return Result.Fail<CorrectKycFieldResult>(CorrectionErrors.IdentityDocumentNotFound);

        var fieldName = cmd.FieldName.Trim();
        var source = cmd.Source.Trim().ToUpperInvariant();

        // The OCR field set is the working copy the scorer reads; MrzDataJson stays as the machine
        // read it, untouched evidence. A correction attributed to the MRZ therefore still lands
        // here — Source records which reading was wrong, not which column is edited.
        var fields = ReadFields(document.OcrFieldsJson);
        fields.TryGetValue(fieldName, out var previousValue);
        fields[fieldName] = cmd.NewValue;

        document.ReplaceOcrFields(JsonSerializer.Serialize(fields));

        var correction = KycFieldCorrection.Create(
            tenantId: file.TenantId,
            kycFileId: file.Id,
            fieldName: fieldName,
            source: source,
            // Both sides encrypted. A corrections table in clear would be the one place the whole
            // point of encrypting the document is undone: "previous = CI0012345678" is the number.
            encryptedPreviousValue: encryptor.Encrypt(previousValue),
            encryptedNewValue: encryptor.Encrypt(cmd.NewValue)!,
            correctedBy: cmd.CorrectedBy,
            clock: clock);

        db.KycFieldCorrections.Add(correction);

        var scored = await ScoreAsync(file, document, fields, fieldName, ct);

        if (scored.IsSuccess)
        {
            var score = scored.Value;

            // Appended, never updated. The history is what shows what the agent changed and what
            // it changed the score to; overwriting the previous assessment would leave the file
            // looking as if it had always scored this well.
            db.KycConfidenceAssessments.Add(KycConfidenceAssessment.Create(
                tenantId: file.TenantId,
                kycFileId: file.Id,
                globalScore: score.Score,
                level: ToKycLevel(score.Level),
                trigger: KycAssessmentTriggers.FieldCorrection,
                clock: clock,
                breakdownJson: JsonSerializer.Serialize(score.Breakdown),
                flagsJson: JsonSerializer.Serialize(score.Flags),
                serviceVersion: score.ServiceVersion));

            // Refresh the file's cached score — through RecordRescore, which touches the snapshot
            // and nothing else. RecordVerification would have been wrong here: it also drives the
            // status machine, and a field correction must not advance a file into the approval
            // circuit behind the agent's back. Leaving the snapshot stale was equally wrong: every
            // screen reads ConfidenceScore from the file, so the agent who had just fixed a
            // misread name would still see the score that misread produced.
            file.RecordRescore(score.Score, ToKycLevel(score.Level), clock);
        }

        await db.SaveChangesAsync(ct);

        return Result.Ok(new CorrectKycFieldResult(
            CorrectionId: correction.Id,
            NewScore: scored.IsSuccess ? scored.Value.Score : null,
            NewConfidenceLevel: scored.IsSuccess ? ToKycLevel(scored.Value.Level).ToString() : null,
            // Rejected and Unavailable both leave the score stale, and both are reported rather
            // than thrown: the correction itself succeeded and must not be rolled back because an
            // external service had an opinion about the photo.
            ScoreUnavailableCode: scored.IsSuccess ? null : scored.Code));
    }

    private Task<BiometryResult<ConfidenceScore>> ScoreAsync(
        KycFile file,
        KycIdentityDocument document,
        Dictionary<string, string> fields,
        string correctedFieldName,
        CancellationToken ct)
    {
        var reading = new OcrReading(
            DocumentType: document.DocType,
            Fields: fields,
            // The corrected field is reported at full confidence: an agent read it off the
            // document with their own eyes, which is strictly better evidence than the OCR's own
            // guess. The other fields' confidences were never persisted, so they are simply
            // absent rather than invented — the contract allows a field to be missing here.
            FieldConfidences: new Dictionary<string, double> { [correctedFieldName] = 1.0 },
            // Not re-submitted: the MRZ is unchanged evidence and re-deriving it from the
            // corrected fields would hand the scorer a zone the document does not carry.
            Mrz: null,
            ServiceVersion: document.ServiceVersion ?? string.Empty);

        return biometry.ScoreAsync(
            file.TenantId,
            new ScoreRequest(
                Ocr: reading,
                // The face match is unchanged evidence the scorer already weighed. It is stored
                // here as JSON quality scores rather than as the scalars ScoreRequest wants, so
                // reconstructing a FaceMatch would mean inventing the quality numbers.
                FaceMatch: null,
                DeclaredFields: fields,
                DocumentType: document.DocType),
            // The file id, so a support request can be traced across the two deployments' logs.
            correlationId: file.Id.ToString(),
            ct);
    }

    /// <summary>
    /// The biometry service has no "rejected" confidence level — that outcome arrives as a
    /// <see cref="BiometryOutcome.Rejected"/> result, never as a level — so the mapping is total.
    /// </summary>
    private static KycConfidenceLevel ToKycLevel(BiometryConfidenceLevel level) => level switch
    {
        BiometryConfidenceLevel.Low => KycConfidenceLevel.Low,
        BiometryConfidenceLevel.Medium => KycConfidenceLevel.Medium,
        _ => KycConfidenceLevel.High,
    };

    /// <summary>
    /// Tolerant on purpose: the column is written by the verification slice and a file that has
    /// never been read, or whose JSON is not a flat string map, must still be correctable — an
    /// agent typing the right value is the recovery path, not a second failure.
    /// </summary>
    private static Dictionary<string, string> ReadFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// The <c>Trigger</c> this slice writes on a <see cref="KycConfidenceAssessment"/>. Only the one
/// value: the verification slice owns <c>VERIFICATION</c> and declaring it here too would be two
/// definitions of one contract.
/// </summary>
internal static class KycAssessmentTriggers
{
    public const string FieldCorrection = "FIELD_CORRECTION";
}
