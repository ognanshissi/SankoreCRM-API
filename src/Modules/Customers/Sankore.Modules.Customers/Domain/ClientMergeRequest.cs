namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A four-eyes request to merge two clients. Merging is irreversible, so the person who asks
/// for it can never be the person who approves it (<see cref="CustomerErrors.SelfApprovalForbidden"/>),
/// and a request can only be decided once (<see cref="CustomerErrors.MergeAlreadyDecided"/>).
/// </summary>
public sealed class ClientMergeRequest : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid SurvivorClientId { get; private set; }
    public Guid AbsorbedClientId { get; private set; }

    /// <summary>Per-field choice (survivor vs absorbed) captured when the request was opened.</summary>
    public string FieldChoicesJson { get; private set; } = default!;

    public MergeRequestStatus Status { get; private set; }
    public Guid? WorkflowInstanceId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecisionComment { get; private set; }
    public DateTimeOffset? ExecutedAt { get; private set; }

    private ClientMergeRequest() { } // EF Core

    public static ClientMergeRequest Open(
        Guid tenantId,
        Guid survivorId,
        Guid absorbedId,
        string fieldChoicesJson,
        Guid requestedBy)
    {
        if (survivorId == Guid.Empty || absorbedId == Guid.Empty)
            throw new DomainException("Both clients are required.", "ClientMergeRequest.Clients.Required");
        if (survivorId == absorbedId)
            throw new DomainException("A client cannot be merged into itself.", "ClientMergeRequest.Self.Forbidden");

        return new ClientMergeRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SurvivorClientId = survivorId,
            AbsorbedClientId = absorbedId,
            FieldChoicesJson = string.IsNullOrWhiteSpace(fieldChoicesJson) ? "{}" : fieldChoicesJson,
            Status = MergeRequestStatus.PendingApproval,
            RequestedBy = requestedBy,
            RequestedAt = DateTimeOffset.UtcNow,
        };
    }

    public void LinkWorkflow(Guid instanceId) => WorkflowInstanceId = instanceId;

    public Result Approve(Guid approverId, string? comment, DateTimeOffset at)
    {
        if (Status != MergeRequestStatus.PendingApproval)
            return Result.Fail(CustomerErrors.MergeAlreadyDecided);
        if (approverId == RequestedBy)
            return Result.Fail(CustomerErrors.SelfApprovalForbidden);

        Status = MergeRequestStatus.Approved;
        DecidedBy = approverId;
        DecidedAt = at;
        DecisionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        return Result.Ok();
    }

    public Result Reject(Guid approverId, string? comment, DateTimeOffset at)
    {
        if (Status != MergeRequestStatus.PendingApproval)
            return Result.Fail(CustomerErrors.MergeAlreadyDecided);

        Status = MergeRequestStatus.Rejected;
        DecidedBy = approverId;
        DecidedAt = at;
        DecisionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        return Result.Ok();
    }

    public Result MarkExecuted(DateTimeOffset at)
    {
        if (Status != MergeRequestStatus.Approved)
            return Result.Fail(CustomerErrors.InvalidStatusTransition);

        Status = MergeRequestStatus.Executed;
        ExecutedAt = at;
        return Result.Ok();
    }
}
