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
    Agency,        // prospect captured in-branch by an agent (was WalkIn)
    CallCenter,    // inbound or outbound call centre capture (was InboundCall)
    Sms,
    Ussd,
    WhatsApp,
    Referral,
    Partner,
    FileImport,
    Campaign       // any marketing / mass-campaign origin (merged SmsUssdCampaign + MarketingCampaign)
}

public enum DispatchingStrategy
{
    RoundRobin,
    WeightedRoundRobin,
    CherryPicking,
    CompatibilityScoring,
    StickyAssignment
}
