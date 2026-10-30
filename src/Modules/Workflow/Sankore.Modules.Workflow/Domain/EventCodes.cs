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

    /// <summary>
    /// A child workflow instance completed — fires on the parent to resume it.
    /// Template designers define a transition on this event to control the next state.
    /// </summary>
    public const string ChildCompleted = "CHILD_COMPLETED";

    /// <summary>A « Notification » step's message was queued for one audience (history only).</summary>
    public const string NotificationSent = "NOTIFICATION_SENT";

    /// <summary>A « Notification » step could not reach one audience, with the reason (history only).</summary>
    public const string NotificationNotSent = "NOTIFICATION_NOT_SENT";

    /// <summary>An « Attente » step ended — delay elapsed or awaited event received (history only).</summary>
    public const string WaitResumed = "WAIT_RESUMED";

    /// <summary>The CRM task of a « Tâche manuelle » step was created (history only, from Leads).</summary>
    public const string TaskCreated = "TASK_CREATED";

    /// <summary>The CRM task of a « Tâche manuelle » step was cancelled (history only).</summary>
    public const string TaskCancelled = "TASK_CANCELLED";

    /// <summary>A « Condition » step was evaluated: which branch, on which value (history only).</summary>
    public const string ConditionResolved = "CONDITION_RESOLVED";

    /// <summary>A « Vérification KYC » step passed: the client's KYC reached the level (history only).</summary>
    public const string KycValidated = "KYC_VALIDATED";

    /// <summary>A « Vérification KYC » step is waiting, or its client's KYC was refused (history only).</summary>
    public const string KycPending = "KYC_PENDING";

    /// <summary>An « Action Integration » step passed: the back office has the record (history only).</summary>
    public const string IntegrationSynced = "INTEGRATION_SYNCED";

    /// <summary>An « Action Integration » step waits, or the back office refused (history only).</summary>
    public const string IntegrationPending = "INTEGRATION_PENDING";

    /// <summary>An overdue step was handed to someone else — supervisor or another role holder.</summary>
    public const string Escalated = "ESCALATED";

    /// <summary>An overdue step stayed where it was and its people were reminded.</summary>
    public const string SlaReminder = "SLA_REMINDER";
}
