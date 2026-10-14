namespace Sankore.Modules.Notifications.Infrastructure.Sms;

/// <summary>
/// Its own section rather than sharing the email processor's. The two channels have different
/// urgency and different costs, and the defaults say so.
/// </summary>
internal sealed class SmsOutboxProcessorOptions
{
    public const string SectionName = "Notifications:SmsOutboxProcessor";

    /// <summary>
    /// Smaller than the email batch. SMS volume is a fraction of email volume, and a large batch
    /// on a per-message channel means a longer window in which a misconfiguration spends money.
    /// </summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// TEN seconds against the email processor's thirty, and the reason is the one-time code: a
    /// customer staring at a code entry screen will not wait half a minute for a message they
    /// expect instantly, and a second-factor flow that feels broken is one people abandon.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// TWO attempts, against the email processor's three.
    ///
    /// <para>
    /// Every attempt on this channel may be billed — a provider that accepted the message and
    /// then timed out on the response has charged for it, and retrying sends a second one. Two is
    /// enough to survive one network blip and no more; a genuinely unreachable provider is an
    /// incident, not something to pay for three times. A one-time code that fails twice is also
    /// something the customer can ask to be re-sent, which is cheaper than this platform guessing.
    /// </para>
    /// </summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>
    /// Minutes after which a claim left in <c>Sending</c> is reclaimed. Longer than the email
    /// equivalent: a double-send here costs money and annoys a customer, so the bar for deciding
    /// a claim is abandoned is deliberately higher.
    /// </summary>
    public int StuckSendingTimeoutMinutes { get; set; } = 10;
}
