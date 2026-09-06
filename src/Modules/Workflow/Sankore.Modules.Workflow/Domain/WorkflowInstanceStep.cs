using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Represents a concrete step within a running <see cref="WorkflowInstance"/>.
/// Created from a <see cref="WorkflowStepDefinition"/> when the instance is started.
/// </summary>
public sealed class WorkflowInstanceStep
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid InstanceId { get; private set; }
    public Guid StepDefinitionId { get; private set; }
    public int Order { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Role code required to act on this step (copied from the definition).</summary>
    public string? ApproverRoleCode { get; private set; }

    /// <summary>SLA timeout copied from the step definition. Null means no deadline.</summary>
    public int? SlaHours { get; private set; }

    /// <summary>Absolute deadline computed when the step enters AwaitingApproval. Null when no SLA.</summary>
    public DateTimeOffset? DueAt { get; private set; }

    public StepStatus Status { get; private set; }
    public Guid? ActedByUserId { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private WorkflowInstanceStep() { }

    public static WorkflowInstanceStep Create(
        Guid tenantId,
        Guid instanceId,
        WorkflowStepDefinition definition)
    {
        return new WorkflowInstanceStep
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InstanceId = instanceId,
            StepDefinitionId = definition.Id,
            Order = definition.Order,
            Name = definition.Name,
            ApproverRoleCode = definition.ApproverRoleCode,
            SlaHours = definition.TimeoutHours,
            Status = StepStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public void StartReview()
    {
        if (Status != StepStatus.Pending)
            throw new DomainException("Step is not in Pending state.");
        Status = StepStatus.AwaitingApproval;
        if (SlaHours.HasValue)
            DueAt = DateTimeOffset.UtcNow.AddHours(SlaHours.Value);
    }

    public void MarkTimedOut()
    {
        if (Status != StepStatus.AwaitingApproval)
            throw new DomainException("Only an active step can time out.");
        Status = StepStatus.TimedOut;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Approve(Guid actedByUserId, string? comment = null)
    {
        if (Status != StepStatus.AwaitingApproval)
            throw new DomainException("Step is not awaiting approval.");
        Status = StepStatus.Approved;
        ActedByUserId = actedByUserId;
        Comment = comment;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Reject(Guid actedByUserId, string? comment = null)
    {
        if (Status != StepStatus.AwaitingApproval)
            throw new DomainException("Step is not awaiting approval.");
        Status = StepStatus.Rejected;
        ActedByUserId = actedByUserId;
        Comment = comment;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Skip()
    {
        Status = StepStatus.Skipped;
        CompletedAt = DateTimeOffset.UtcNow;
    }
}
