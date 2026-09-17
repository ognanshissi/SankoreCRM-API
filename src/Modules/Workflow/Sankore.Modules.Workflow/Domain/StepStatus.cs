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
    /// <summary>Step bypassed by a SkipIf or RequireIf rule.</summary>
    Skipped,
    /// <summary>Step approved automatically by an AutoApproveIf rule.</summary>
    AutoApproved,
    /// <summary>Step exceeded its SLA deadline without a decision.</summary>
    TimedOut
}
