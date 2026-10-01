namespace Sankore.Modules.Kyc.Features.Approval.GetCircuit;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// What the approval panel of a file shows: who must sign, in what order, and where it stands.
/// A query — no <c>ICommand</c>, so neither the transaction nor the audit behaviour runs.
/// </summary>
internal sealed record GetKycApprovalCircuitQuery(Guid KycFileId)
    : IRequest<Result<KycApprovalCircuitDto>>;

/// <param name="IsStarted">
/// False while the file has not reached validation yet. The levels are then the circuit it WOULD
/// climb, computed from its current vigilance — a preview, not a promise: a duplicate flag raised
/// before the verification completes still adds the compliance officer.
/// </param>
/// <param name="NextLevel">
/// The rung waiting for a signature, or null when the circuit is finished or not yet open. The
/// decision endpoint accepts this level and no other.
/// </param>
public sealed record KycApprovalCircuitDto(
    Guid KycFileId,
    string FileStatus,
    string VigilanceLevel,
    bool DuplicateSuspected,
    int FaceMatchAttempts,
    bool IsStarted,
    string? NextLevel,
    IReadOnlyList<KycApprovalStepDto> Steps);

/// <summary>
/// One rung. Carries the approver's id and comment — a motive written for operators — and never a
/// field of the file: this DTO is read by anyone holding <c>kyc:read</c>.
/// </summary>
public sealed record KycApprovalStepDto(
    string Level,
    string Decision,
    Guid? ApproverId,
    string? Comment,
    DateTimeOffset? DecidedAt);
