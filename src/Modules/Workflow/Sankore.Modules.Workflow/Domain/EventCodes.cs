namespace Sankore.Modules.Workflow.Domain;

/// <summary>Well-known event codes used by the workflow state machine.</summary>
public static class EventCodes
{
    /// <summary>Automatic transition — no human input required.</summary>
    public const string Auto    = "AUTO";

    /// <summary>Approver approved the current state.</summary>
    public const string Approve = "APPROVE";

    /// <summary>Approver rejected the current state.</summary>
    public const string Reject  = "REJECT";

    /// <summary>Instance was manually cancelled.</summary>
    public const string Cancel  = "CANCEL";

    /// <summary>SLA deadline exceeded without a decision.</summary>
    public const string Timeout = "TIMEOUT";

    /// <summary>A linked WorkflowTask was completed by an agent.</summary>
    public const string TaskCompleted = "TASK_COMPLETED";

    /// <summary>A step was explicitly assigned to a specific user (audit only, not a state-machine event).</summary>
    public const string Assign = "ASSIGN";

    /// <summary>Current assignee delegated the step to another user (audit only, not a state-machine event).</summary>
    public const string Delegate = "DELEGATE";
}
