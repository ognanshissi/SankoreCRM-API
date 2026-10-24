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

/// <summary>
/// Which <see cref="PipelineStage"/> values a LEAD still moves through (projects, lot 4). The
/// contact stages, up to « besoin identifié »; what came after — qualification, proposal, the
/// application, its documents and its approval — is now a PROJECT's steps
/// (<see cref="ProjectTemplate"/>). The values stay in the enum: they are stored on old rows and in
/// event history, and the migration <c>MoveLeadApplicationStagesToProjects</c> moved every open
/// lead off them.
/// </summary>
public static class LeadPipeline
{
    public static readonly IReadOnlyList<PipelineStage> ContactStages =
    [
        PipelineStage.New,
        PipelineStage.ContactAttempted,
        PipelineStage.ContactEstablished,
        PipelineStage.NeedIdentified,
    ];

    /// <summary>The stages a report shows for leads: the contact stages and the two outcomes.</summary>
    public static readonly IReadOnlyList<PipelineStage> ReportStages =
        [.. ContactStages, PipelineStage.Converted, PipelineStage.Lost];

    /// <summary>A stage that belongs to a project now, and that a lead may no longer be moved to.</summary>
    public static bool IsProjectStage(PipelineStage stage)
        => stage is PipelineStage.Qualified or PipelineStage.ProductProposed or PipelineStage.ApplicationStarted
            or PipelineStage.DocumentCollection or PipelineStage.ApplicationCompleted or PipelineStage.ApprovalPending;

    public const string StageIsAProjectStep = "PIPELINE_STAGE_IS_A_PROJECT_STEP";

    /// <summary>A lead converts when one of its projects is approved, not by a stage change.</summary>
    public const string ConvertedByProjectApproval = "PIPELINE_STAGE_CONVERTED_BY_PROJECT_APPROVAL";
}
