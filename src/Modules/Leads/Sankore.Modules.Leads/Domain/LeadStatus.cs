namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// High-level business lifecycle state of a Lead (M13 spec).
/// Separate from <see cref="PipelineStage"/> which tracks the granular sales funnel position.
/// </summary>
public enum LeadStatus
{
    New,
    Open,
    Qualifying,
    Qualified,      // formerly SalesQualified — eligible for dispatching
    Nurturing,
    Recycled,
    Converted,
    Lost,
    Disqualified,
    Archived
}

public enum LeadSource
{
    Web,
    MobileAgent,
    WalkIn,
    SmsUssdCampaign,
    Referral,
    FileImport,
    Partner,
    InboundCall,
    WhatsApp,
    MarketingCampaign
}

public enum DispatchingStrategy
{
    RoundRobin,
    WeightedRoundRobin,
    CherryPicking,
    CompatibilityScoring,
    StickyAssignment
}
