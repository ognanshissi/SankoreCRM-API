namespace Sankore.Modules.Kyc.Features.Corrections.CorrectField;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// KYC-B-04 — an agent fixes a field the machine misread.
///
/// <para>
/// <paramref name="NewValue"/> carries <see cref="SensitiveDataAttribute"/> because an OCR field
/// is a name, a date of birth or a document number depending on which field it is. The audit
/// trail must prove WHO changed WHICH field and WHEN; recording the value itself would put the
/// very data the document store encrypts into the audit log in clear — and the audit log is the
/// one table that is read by people who are not entitled to the file.
/// </para>
///
/// <para>
/// <paramref name="FieldName"/> is NOT sensitive and must not be hidden: without it the trail
/// proves nothing. That asymmetry — names visible, values masked — is the whole design.
/// </para>
/// </summary>
/// <param name="Source">
/// Which machine reading produced the wrong value: <c>OCR</c> or <c>MRZ</c>. Provenance only —
/// the corrected value always lands in the document's OCR fields, which are what the scorer reads.
/// </param>
internal sealed record CorrectKycFieldCommand(
    Guid KycFileId,
    string FieldName,
    string Source,
    [property: SensitiveData] string NewValue,
    Guid CorrectedBy
) : IRequest<Result<CorrectKycFieldResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => KycFileId.ToString();
}

/// <param name="NewScore">
/// Null when the scorer could not be reached. The correction still stands — it is the agent's
/// own factual statement about the document, and losing it because an external service is down
/// would make the agent type it again.
/// </param>
/// <param name="ScoreUnavailableCode">
/// The biometry code explaining why no assessment was appended, null on success. Surfaced so the
/// screen can say "corrigé, score à recalculer" instead of silently showing a stale score.
/// </param>
internal sealed record CorrectKycFieldResult(
    Guid CorrectionId,
    int? NewScore,
    string? NewConfidenceLevel,
    string? ScoreUnavailableCode);

/// <summary>
/// Codes this slice adds to <see cref="Domain.KycErrors"/>.
///
/// They live here rather than in <c>KycErrors</c> because that file is owned by another task in
/// this round; they follow the same UPPER_SNAKE contract and move there on the next pass.
/// </summary>
internal static class CorrectionErrors
{
    /// <summary>There is nothing to correct: the file carries no identity document yet.</summary>
    public const string IdentityDocumentNotFound = Domain.KycErrors.IdentityDocumentNotFound;

    /// <summary>A correction must say where the wrong value came from — <c>OCR</c> or <c>MRZ</c>.</summary>
    public const string SourceInvalid = Domain.KycErrors.CorrectionSourceInvalid;
}

/// <summary>The two machine readings a correction can be attributed to.</summary>
internal static class KycCorrectionSources
{
    public const string Ocr = "OCR";
    public const string Mrz = "MRZ";

    public static bool IsKnown(string? source)
        => source is not null
           && (source.Equals(Ocr, StringComparison.OrdinalIgnoreCase)
               || source.Equals(Mrz, StringComparison.OrdinalIgnoreCase));
}
