namespace Sankore.Modules.Leads.Domain;

public enum LeadType
{
    Individual,
    Corporate
}

public enum LeadIntentLevel
{
    Unknown,
    Cold,
    Warm,
    Hot
}

public enum LeadGender
{
    Unknown,
    Male,
    Female,
    Other
}

public enum LeadChannel
{
    Web,
    MobileAgent,
    Agency,
    CallCenter,
    Sms,
    Ussd,
    WhatsApp,
    Referral,
    Partner,
    Import,
    MarketingCampaign
}

public enum LeadCloseReason
{
    Lost,
    Disqualified,
    Archived
}

public enum ActivityType
{
    Call,
    Meeting,
    Email,
    Visit,
    Note,
    Sms,
    WhatsApp,
    Task
}

public enum ActivityOutcome
{
    Reached,
    NoAnswer,
    Voicemail,
    Callback,
    Interested,
    NotInterested,
    Rescheduled,
    Completed
}

public enum ReminderStatus
{
    Pending,
    Completed,
    Dismissed
}

/// <summary>
/// How a lead's first contact came to be recorded (TEL-B-12).
///
/// <para>
/// <b>Why the source is worth a column.</b> <c>FirstContactAt</c> stops an SLA clock, and the two
/// ways it gets set are not equally trustworthy: an agent pressing "I called them" is a claim,
/// while a telephony call that the provider reported and the agent then qualified is evidence.
/// Without the distinction, a manager looking at a contact rate cannot tell a team that answers
/// its leads from one that has learned to press the button — and the figure is the one every
/// commercial target is set against.
/// </para>
///
/// <para>
/// Nullable on the row, because it is null for every assignment contacted before this existed.
/// "Recorded before we tracked how" is a different fact from either value, and backfilling one
/// would manufacture evidence.
/// </para>
/// </summary>
public enum FirstContactSource
{
    /// <summary>An agent declared it, through the back office.</summary>
    AgentDeclared,

    /// <summary>A telephony call was qualified with an outcome that counts as a contact.</summary>
    TelephonyCall,
}
