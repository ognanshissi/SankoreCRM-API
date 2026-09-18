namespace Sankore.Modules.Leads.Domain;

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
