namespace Sankore.Modules.Leads.Domain;

/// <summary>Typed catalogue of recommended next actions for a lead.</summary>
public enum NextActionType
{
    /// <summary>Lead has never been scored — run the qualification form.</summary>
    Qualify,
    /// <summary>Score ≥ 60 and lead is not yet assigned to an agent.</summary>
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
    Disqualify
}
