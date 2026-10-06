namespace Sankore.Modules.Kyc.Features.Verification.ManualValidation;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Validates a file's evidence by hand, in place of a biometric score, and sends it into the
/// approval circuit.
///
/// <para>
/// It lives beside <c>RunVerification</c> rather than under <c>Documents</c> because it is the human
/// counterpart of a verification: same entry statuses, same effect on the file, same
/// <c>StartKycApprovalCommand</c> tail. What it is not is a document decision — those are
/// per-image and live in their own slice.
/// </para>
///
/// <para>
/// The validator is <c>ICurrentUser</c>, never a field: it is recorded on the file as a four-eyes
/// anchor, so a caller-supplied id would let somebody nominate a colleague as the person barred
/// from approving.
/// </para>
/// </summary>
/// <param name="Reason">
/// On what grounds the machine was overridden. Mandatory — this is the whole evidence that the
/// override was a decision and not a click, and it is read by whoever signs the ladder afterwards.
///
/// <para>
/// An operator's motive, like <c>KycApprovalStep.Comment</c>: deliberately not redacted from
/// <c>audit.entries</c>, and therefore never a place for customer data.
/// </para>
/// </param>
internal sealed record ManuallyValidateKycFileCommand(
    Guid KycFileId,
    string Reason
) : IRequest<Result<ManuallyValidateKycFileResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => KycFileId.ToString();
}

/// <param name="ApprovalLevels">
/// The ladder the file must now climb. Carries the branch manager even on a low-risk file: the
/// validator who asserted the evidence cannot sign for it, so an agent-only ladder would leave the
/// file unsignable.
/// </param>
internal sealed record ManuallyValidateKycFileResult(
    Guid KycFileId,
    string FileStatus,
    IReadOnlyList<string> ApprovalLevels);
