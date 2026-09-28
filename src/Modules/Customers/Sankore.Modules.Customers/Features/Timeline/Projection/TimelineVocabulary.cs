namespace Sankore.Modules.Customers.Features.Timeline.Projection;

/// <summary>
/// Value of <c>ClientTimelineEntry.SourceModule</c>. A free string on purpose: a new module
/// starts feeding the timeline by adding a constant here and a consumer — the table, the
/// projector and the read endpoint stay untouched.
/// </summary>
public static class TimelineSourceModules
{
    public const string Customers = "Customers";
    public const string Leads = "Leads";

    // Reserved for the modules that will plug in later (M02 KYC, M03 Savings,
    // M04 Credit, M08 Notifications). Declared now so the filter values of
    // GET clients/{id}/timeline are stable from day one.
    public const string Kyc = "Kyc";
    public const string Savings = "Savings";
    public const string Credit = "Credit";
    public const string Notifications = "Notifications";
}

/// <summary>
/// Values of <c>ClientTimelineEntry.EntryType</c> produced by module M01 itself.
/// UPPER_SNAKE, stable: the front-end maps them to an icon and a localized label.
/// </summary>
public static class ClientTimelineEntryTypes
{
    public const string ClientCreated = "CLIENT_CREATED";
    public const string ClientActivated = "CLIENT_ACTIVATED";
    public const string ClientSuspended = "CLIENT_SUSPENDED";
    public const string ClientArchived = "CLIENT_ARCHIVED";
    public const string ClientTransferred = "CLIENT_TRANSFERRED";
    public const string ClientsMerged = "CLIENTS_MERGED";
    public const string SegmentChanged = "SEGMENT_CHANGED";
    public const string GroupMembershipChanged = "GROUP_MEMBERSHIP_CHANGED";

    /// <summary>Produced by the retention job of the Compliance zone (US-M01-BE-29).</summary>
    public const string RetentionEligible = "RETENTION_ELIGIBLE";
}
