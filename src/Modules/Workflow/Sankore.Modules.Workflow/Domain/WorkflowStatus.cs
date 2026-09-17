namespace Sankore.Modules.Workflow.Domain;

public enum WorkflowStatus
{
    /// <summary>Instance created, waiting for first step.</summary>
    Pending,
    /// <summary>At least one step is in progress.</summary>
    InProgress,
    /// <summary>All steps approved — workflow finished successfully.</summary>
    Completed,
    /// <summary>A step was rejected — workflow stopped.</summary>
    Rejected,
    /// <summary>Manually cancelled before completion.</summary>
    Cancelled,
    /// <summary>A step exceeded its SLA deadline — workflow stopped.</summary>
    TimedOut,

    /// <summary>
    /// Workflow is suspended waiting for a child workflow instance to complete.
    /// Resumes automatically when the child fires <see cref="EventCodes.ChildCompleted"/>.
    /// </summary>
    WaitingForChild
}
