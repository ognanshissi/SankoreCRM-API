namespace Sankore.Modules.Kyc.Features.Verification.RunVerification;

using MediatR;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Runs the biometric verification of a KYC file (KYC-B-03) and persists everything it produced.
///
/// <para>
/// The images are NOT carried here. They are already in the encrypted document store and the
/// command takes their opaque references: this record is serialised into the audit trail by
/// <see cref="AuditBehavior{TRequest,TResponse}"/> and replayed through a Hangfire payload, and
/// neither place may ever hold a photograph of someone's identity card. The same reasoning
/// excludes the agent's declared fields — they are a name, a date of birth, a document number.
/// </para>
/// </summary>
/// <param name="DocumentStorageRef">
/// Reference issued by <c>IKycDocumentStore</c> for the identity-document image. Opaque by
/// construction: it reveals neither the tenant, nor the file, nor the kind of document.
/// </param>
/// <param name="RequestedBy">
/// Who is held accountable for the submission. It becomes the file's <c>LastSubmittedBy</c>, which
/// is what the four-eyes rule later refuses to accept as the approver — so a SYSTEM placeholder
/// would quietly hand the approval back to whoever clicks first.
/// </param>
/// <param name="Attempt">
/// 1 for an agent's request, incremented by <see cref="ReplayKycVerificationJob"/> each time the
/// biometric service could not be reached. It is the only thing that stops an outage from
/// re-queuing itself forever; see <see cref="RunKycVerificationHandler"/>.
/// </param>
internal sealed record RunKycVerificationCommand(
    Guid TenantId,
    Guid KycFileId,
    string DocumentStorageRef,
    string SelfieStorageRef,
    Guid RequestedBy,
    int Attempt = 1
) : IRequest<Result<RunKycVerificationResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => KycFileId.ToString();
}

/// <summary>
/// What a verification run ended as. Three values, because the biometric boundary has three
/// outcomes and collapsing any two of them breaks a file's lifecycle — see
/// <c>BiometryOutcome</c> for the long form.
/// </summary>
internal enum RunKycVerificationOutcome
{
    /// <summary>The service scored the file; it is routed into the approval circuit, or back to
    /// the agent when the score is a refusal.</summary>
    Scored,

    /// <summary>The service says the capture is unusable. The file is back with the agent and
    /// <see cref="RunKycVerificationResult.Code"/> says what to re-shoot.</summary>
    CaptureRejected,

    /// <summary>We learned nothing. The file stays in Verifying and a replay is queued.</summary>
    ServiceUnavailable,
}

/// <param name="Code">
/// The biometric code for a rejected capture or an unreachable service, null on a score. A code
/// and never a sentence: the front translates it, and the French wording is not this layer's.
/// </param>
internal sealed record RunKycVerificationResult(
    Guid KycFileId,
    KycFileStatus Status,
    RunKycVerificationOutcome Outcome,
    int? ConfidenceScore,
    KycConfidenceLevel? ConfidenceLevel,
    string? Code);

/// <summary>
/// Error codes this slice adds to <see cref="KycErrors"/>. They live here rather than in the
/// shared list because nothing outside this slice can produce them — and like every code in this
/// codebase, renaming one is a breaking API change.
/// </summary>
internal static class RunKycVerificationErrors
{
    /// <summary>A storage reference points at nothing, or at another tenant's object. The two are
    /// deliberately indistinguishable — the store returns null for both.</summary>
    public const string ImageNotFound = Domain.KycErrors.VerificationImageNotFound;
}
