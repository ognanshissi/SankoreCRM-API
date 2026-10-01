namespace Sankore.Modules.Kyc.Features.Approval.StartApproval;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens the approval circuit of a file that has just reached validation.
///
/// <para>
/// There is deliberately NO endpoint for it. A circuit is not something an operator starts; it is
/// what a verification outcome produces, and exposing it would let a file gain a second set of
/// steps from a button. The verification slice sends this command, and so does anything else that
/// moves a file into <c>Validating</c>.
/// </para>
///
/// <para>
/// <paramref name="TenantId"/> is explicit rather than read from the ambient context: the sender
/// may be a MassTransit consumer or a Hangfire replay, neither of which runs inside an HTTP
/// request.
/// </para>
/// </summary>
/// <param name="StartedBy">
/// Who caused the file to enter validation — the agent whose verification produced the score. Used
/// as the initiator of the traceability workflow, never as an approver.
/// </param>
internal sealed record StartKycApprovalCommand(
    Guid TenantId,
    Guid KycFileId,
    Guid StartedBy
) : IRequest<Result<StartKycApprovalResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => KycFileId.ToString();
}

/// <param name="AlreadyStarted">
/// True when the circuit was already there. The command succeeds in that case — the caller asked
/// for the file to have a circuit and it has one — but a caller uses this to stay silent instead
/// of notifying the same approver twice on a redelivered event.
/// </param>
/// <param name="WorkflowInstanceId">
/// The M12 instance opened for traceability, or null when none could be started. Null is NOT an
/// error: the circuit is enforced here, the workflow only records it.
/// </param>
internal sealed record StartKycApprovalResult(
    Guid KycFileId,
    IReadOnlyList<string> Levels,
    bool AlreadyStarted,
    Guid? WorkflowInstanceId);
