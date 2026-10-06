namespace Sankore.Modules.Kyc.Features.Documents.ReviewDocument;

using MediatR;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// One validator's verdict on one uploaded image.
///
/// <para>
/// The validator is NOT a field of this command: it is <c>ICurrentUser</c>, read in the handler.
/// The same rule the approval circuit applies — a caller-supplied actor is an attribution anybody
/// can forge by typing somebody else's id, and attribution is the whole point of recording a
/// decision.
/// </para>
/// </summary>
/// <param name="Decision">
/// <c>Accepted</c> or <c>Refused</c>. <c>Pending</c> and <c>NotReviewed</c> are states and are
/// refused by the validator — sending one would be asking to un-review a document.
/// </param>
/// <param name="Reason">
/// Why the document was refused. Mandatory for a refusal, ignored for an acceptance: a refusal is a
/// to-do list for the agent who has to produce a better image, and an acceptance has nothing to
/// explain to anybody.
///
/// <para>
/// Deliberately NOT <c>[SensitiveData]</c>, and the validator's message says why: this is an
/// operator's motive, the same category as <c>KycApprovalStep.Comment</c>, and redacting it in
/// <c>audit.entries</c> would destroy the evidence that the refusal was a decision rather than a
/// click. It must therefore never carry a document number, a phone or an address.
/// </para>
/// </param>
internal sealed record ReviewKycDocumentCommand(
    Guid KycFileId,
    Guid DocumentId,
    KycDocumentReviewDecision Decision,
    string? Reason
) : IRequest<Result<ReviewKycDocumentResult>>, ICommand, IResourceCommand
{
    /// <summary>The document, not the file: the decision is about one image.</summary>
    public string ResourceType => "KycDocument";

    public string? ResourceId => DocumentId.ToString();
}

/// <param name="FileStatus">
/// Where the file ended up. Unchanged on an acceptance — by explicit product decision, nothing
/// advances as a side effect of a document edit — and <c>ComplementRequired</c> on a refusal.
/// </param>
internal sealed record ReviewKycDocumentResult(
    Guid DocumentId,
    string Kind,
    string Decision,
    string FileStatus);
