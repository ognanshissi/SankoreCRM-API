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

    /// <summary>
    /// Specific user assigned to work on this step.
    /// When null, any user holding <see cref="ApproverRoleCode"/> may act.
    /// </summary>
    public Guid? AssignedToUserId { get; private set; }

    /// <summary>SLA timeout copied from the step definition. Null means no deadline.</summary>
    public int? SlaHours { get; private set; }

    /// <summary>Absolute deadline computed when the step enters AwaitingApproval. Null when no SLA.</summary>
    public DateTimeOffset? DueAt { get; private set; }

    /// <summary>
    /// Copied from the definition at start, with <see cref="ConfigJson"/>: a running instance keeps
    /// the circuit it was started on, and the engine reads them without loading the template.
    /// </summary>
    public StepKind Kind { get; private set; } = StepKind.Validation;

    public string? ConfigJson { get; private set; }

    /// <summary>Run on entry, decided by nobody — see <see cref="StepKinds.RunsAutomatically"/>.</summary>
    public bool RunsAutomatically => StepKinds.RunsAutomatically(Kind);

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
            Kind = definition.Kind,
            ConfigJson = definition.ConfigJson,
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

    /// <summary>Runs an automatic step, whether the engine reached it pending or already opened it.</summary>
    public void ExecuteAutomatically()
    {
        if (!RunsAutomatically)
            throw new DomainException("Only an automatic step can be executed by the engine.");
        if (Status is not (StepStatus.Pending or StepStatus.AwaitingApproval))
            throw new DomainException("Step has already been completed.");
        Status = StepStatus.Executed;
        DueAt = null;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Ends an « Attente » step: its delay ran out or the awaited event arrived. Nobody decided it,
    /// hence <see cref="StepStatus.Executed"/> like an automatic step.
    /// </summary>
    public void CompleteWait() => CompleteByEngine();

    /// <summary>
    /// Ends an open step the engine settles itself — a wait (delay or event), a condition, a KYC
    /// check whose client reached the level. <see cref="StepStatus.Executed"/>: nobody decided it.
    /// </summary>
    public void CompleteByEngine()
    {
        if (Kind is not (StepKind.Wait or StepKind.Condition or StepKind.KycCheck or StepKind.IntegrationAction))
            throw new DomainException("This step is not settled by the engine.");
        if (Status != StepStatus.AwaitingApproval)
            throw new DomainException("The step is not open.");
        Status = StepStatus.Executed;
        DueAt = null;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>A « Condition » step was evaluated by the engine; nobody decided it.</summary>
    public void CompleteCondition() => CompleteByEngine();

    /// <summary>
    /// The branch of the preceding « Condition » this step belongs to (<c>"branch"</c> in its
    /// configuration, set by the journey builder), or null for a step on the main line.
    /// </summary>
    public string? Branch => StepBranch.Of(ConfigJson);

    /// <summary>
    /// The step's delay ran out and it is escalated: handed to <paramref name="assignTo"/> when the
    /// escalation found someone (else it stays with whoever had it), and given a fresh delay.
    /// </summary>
    public void Escalate(Guid? assignTo, DateTimeOffset now)
    {
        if (Status != StepStatus.AwaitingApproval)
            throw new DomainException("Only an open step can be escalated.");
        if (assignTo is { } user) AssignedToUserId = user;
        DueAt = SlaHours.HasValue ? now.AddHours(SlaHours.Value) : null;
    }

    public void AutoApprove()
    {
        if (Status != StepStatus.Pending)
            throw new DomainException("Only a pending step can be auto-approved.");
        Status = StepStatus.AutoApproved;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Assigns this step to a specific user (admin or system action).
    /// The step must be awaiting approval.
    /// </summary>
    public void Assign(Guid userId)
    {
        if (Status != StepStatus.AwaitingApproval)
            throw new DomainException("Only an active (AwaitingApproval) step can be assigned.");
        AssignedToUserId = userId;
    }

    /// <summary>
    /// Transfers responsibility to <paramref name="toUserId"/>.
    /// The caller must be the current assignee or the operation is rejected.
    /// </summary>
    public void Delegate(Guid fromUserId, Guid toUserId)
    {
        if (Status != StepStatus.AwaitingApproval)
            throw new DomainException("Only an active (AwaitingApproval) step can be delegated.");
        if (AssignedToUserId.HasValue && AssignedToUserId.Value != fromUserId)
            throw new DomainException("You are not the current assignee of this step.");
        AssignedToUserId = toUserId;
    }
}

/// <summary>The two branches of a « Condition » step, as the journey builder writes them.</summary>
public static class StepBranch
{
    public const string Yes = "yes";
    public const string No = "no";

    public static string? Of(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("branch", out var b)
                && b.ValueKind == System.Text.Json.JsonValueKind.String
                && b.GetString() is Yes or No
                    ? b.GetString()
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
