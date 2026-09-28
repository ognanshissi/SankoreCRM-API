namespace Sankore.Modules.Customers.Features.Duplicates.Merge.RequestClientMerge;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens a four-eyes merge request (US-M01-BE-25). Nothing is merged here: the request lands in
/// <c>PendingApproval</c> and waits for a DIFFERENT user to approve it.
/// <para>
/// <see cref="Reason"/> has no column on <c>ClientMergeRequest</c> by design — it belongs to the
/// audit trail, which this command feeds through <see cref="ICommand"/> + <see cref="IResourceCommand"/>.
/// </para>
/// </summary>
public sealed record RequestClientMergeCommand(
    Guid SurvivorClientId,
    Guid AbsorbedClientId,
    IReadOnlyDictionary<string, string> FieldChoices,
    string Reason) : IRequest<Result<RequestClientMergeResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientMergeRequest";

    /// <summary>The request does not exist yet when the command is dispatched.</summary>
    public string? ResourceId => null;
}

/// <summary>
/// <paramref name="WorkflowInstanceId"/> is null when M12 has no <c>ClientMerge</c> template, or
/// when starting the instance failed: the request is still valid and still needs a second pair of
/// eyes, because the four-eyes rule is enforced by M01 itself.
/// </summary>
public sealed record RequestClientMergeResult(Guid MergeRequestId, string Status, Guid? WorkflowInstanceId);
