namespace Sankore.Modules.Notifications.Domain;

/// <summary>
/// Where a queued text message stands. The same five states as the email outbox, so one mental
/// model covers both queues.
/// </summary>
public enum SmsOutboxStatus
{
    Pending,

    /// <summary>Claimed by a processor. A stuck claim is reclaimed after a timeout.</summary>
    Sending,

    /// <summary>
    /// Handed to the provider, which accepted it. NOT "the handset received it" — that is
    /// <c>SmsOutboxMessage.ProviderStatus</c>, which a callback fills in later and which is a
    /// different question with a different answer.
    /// </summary>
    Sent,

    Failed,
    DeadLettered
}
