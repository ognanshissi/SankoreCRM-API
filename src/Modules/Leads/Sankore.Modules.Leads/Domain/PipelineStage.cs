namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Granular position of a Lead in the commercial sales pipeline.
/// Tracks the agent's progress, separate from the high-level <see cref="LeadStatus"/>.
/// </summary>
public enum PipelineStage
{
    New,
    ContactAttempted,
    ContactEstablished,
    NeedIdentified,
    Qualified,
    ProductProposed,
    ApplicationStarted,
    DocumentCollection,
    ApplicationCompleted,
    ApprovalPending,
    Converted,
    Lost
}
