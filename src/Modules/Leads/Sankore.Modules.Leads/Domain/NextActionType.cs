namespace Sankore.Modules.Leads.Domain;

/// <summary>Typed catalogue of recommended next actions for a lead.</summary>
public enum NextActionType
{
    /// <summary>Lead has never been scored — run the qualification form.</summary>
    Qualify,
    /// <summary>Qualified and nobody works it — neither assigned nor owned.</summary>
    Dispatch,
    /// <summary>Score 40-59 — gather missing information before dispatching.</summary>
    CollectMoreData,
    /// <summary>Qualification form partially filled — resume completion.</summary>
    CompleteQualification,
    /// <summary>Assigned but SLA deadline exceeded without first contact.</summary>
    ContactAgain,
    /// <summary>A pending reminder is past due.</summary>
    FollowUp,
    /// <summary>Lead in Nurturing state — maintain regular touchpoints.</summary>
    Nurture,
    /// <summary>Score too low for conversion — consider disqualification.</summary>
    Disqualify,
    /// <summary>Qualified, worked by an agent, no open project — open the project of the product sought.</summary>
    CreateProject,
    /// <summary>Qualified with an open project — what comes next happens in the project.</summary>
    AdvanceProject
}
