namespace Sankore.Modules.Leads.Domain;

public enum LeadStatus
{
    New,
    Contacted,
    MarketingQualified,   // MQL
    SalesQualified,       // SQL — eligible for dispatching
    Assigned,
    Converted,
    Lost,
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
    InboundCall
}

public enum DispatchingStrategy
{
    RoundRobin,
    WeightedRoundRobin,
    CherryPicking,
    CompatibilityScoring,   // default & recommended — see CompatibilityScorer
    StickyAssignment
}
