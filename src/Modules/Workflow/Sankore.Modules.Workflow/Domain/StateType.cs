namespace Sankore.Modules.Workflow.Domain;

public enum StateType
{
    /// <summary>Entry point. Automatically transitions to the first real state.</summary>
    Initial,

    /// <summary>Intermediate state with no human action. Transitions fire automatically.</summary>
    Normal,

    /// <summary>Requires a human approval decision (approve / reject).</summary>
    Approval,

    /// <summary>Pauses until an assigned task is completed.</summary>
    WaitingTask,

    /// <summary>Terminal success state. Workflow completes.</summary>
    Success,

    /// <summary>Terminal rejection state. Workflow stops.</summary>
    Rejected,

    /// <summary>Terminal cancellation state.</summary>
    Cancelled,

    /// <summary>Terminal SLA expiry state.</summary>
    Expired
}
