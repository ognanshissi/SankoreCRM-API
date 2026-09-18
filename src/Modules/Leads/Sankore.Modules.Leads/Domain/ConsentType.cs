namespace Sankore.Modules.Leads.Domain;

/// <summary>Purpose for which the prospect's consent was collected.</summary>
public enum ConsentType
{
    /// <summary>General marketing communications (newsletters, promotions).</summary>
    Marketing,

    /// <summary>Processing personal data to deliver the requested service.</summary>
    DataProcessing,

    /// <summary>Contact via email for commercial or service purposes.</summary>
    EmailContact,

    /// <summary>Contact via SMS / text message.</summary>
    SmsContact,

    /// <summary>Contact by phone (voice calls).</summary>
    PhoneContact,

    /// <summary>Sharing personal data with third-party partners.</summary>
    ThirdPartySharing,

    /// <summary>Behavioural profiling and analytics.</summary>
    ProfilingAndAnalytics
}
