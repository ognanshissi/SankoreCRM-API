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
    ProfilingAndAnalytics,

    /// <summary>Recording GPS location during field visits (E03 — Addendum F13.1 §3.6).</summary>
    LocationTracking,

    /// <summary>
    /// Processing HEALTH data — the explicit consent BNF-B-09 requires before any health
    /// questionnaire may be answered, PER PERSON.
    ///
    /// <para>
    /// A consent type rather than a <c>HealthConsent</c> entity of its own:
    /// <see cref="LeadConsent"/> already carries the status, the channel, the proof reference, the
    /// recorder and a tracked withdrawal, and the nurturing cancellation already reacts to a
    /// withdrawal. A separate entity would have duplicated that machinery and kept health consent
    /// out of the consent screens the agent already uses.
    /// </para>
    /// </summary>
    HealthData
}
