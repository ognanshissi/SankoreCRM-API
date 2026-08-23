namespace Sankore.Modules.Workflow.Domain;

public enum StepStatus
{
    /// <summary>Step not yet started (waiting for previous step).</summary>
    Pending,
    /// <summary>Step is awaiting an approver decision.</summary>
    AwaitingApproval,
    /// <summary>Approver accepted this step.</summary>
    Approved,
    /// <summary>Approver rejected this step.</summary>
    Rejected,
    /// <summary>Step bypassed (e.g. no approver configured).</summary>
    Skipped
}
