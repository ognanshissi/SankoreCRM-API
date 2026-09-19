namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Classifies the nature of a CRM task so that agents and managers can
/// prioritise and route work by type (US-M13-080).
/// </summary>
public enum CrmTaskType
{
    FirstContact,
    Qualification,
    SlaFollowUp,
    ScoreReview,
    OwnerHandover,
    ManualDispatch,
    Generic
}
