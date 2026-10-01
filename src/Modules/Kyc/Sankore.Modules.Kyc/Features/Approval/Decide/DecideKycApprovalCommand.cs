namespace Sankore.Modules.Kyc.Features.Approval.Decide;

using MediatR;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// One signature on one rung of the circuit.
///
/// <para>
/// The approver is NOT a field of the command: it is <c>ICurrentUser</c>, read in the handler. A
/// caller-supplied approver id would be a four-eyes rule anybody can walk around by typing
/// somebody else's id, and the rule is the entire point of this story.
/// </para>
/// </summary>
/// <param name="Level">
/// The rung being signed. Sent explicitly rather than inferred from "the next pending one" so a
/// screen that was left open on a file somebody else has moved on gets
/// <c>KYC_APPROVAL_OUT_OF_ORDER</c> instead of silently signing a rung it was not showing.
/// </param>
/// <param name="Comment">
/// The approver's motive. Mandatory for a refusal or a complement request — it is what the agent
/// and the customer are told — and it travels on <c>KycRejectedEvent</c>, so it must never carry a
/// document number, a phone or an address.
/// </param>
internal sealed record DecideKycApprovalCommand(
    Guid KycFileId,
    KycApprovalLevel Level,
    KycApprovalDecision Decision,
    string? Comment
) : IRequest<Result<DecideKycApprovalResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => KycFileId.ToString();
}

/// <param name="CircuitCompleted">
/// True when this decision closed the circuit — the file was validated, refused, or sent back for
/// a complement. False when the file is simply waiting for the next rung.
/// </param>
internal sealed record DecideKycApprovalResult(
    Guid KycFileId,
    string Level,
    string Decision,
    string FileStatus,
    string Tier,
    bool CircuitCompleted);
