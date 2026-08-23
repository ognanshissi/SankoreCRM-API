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
    Cancelled
}
