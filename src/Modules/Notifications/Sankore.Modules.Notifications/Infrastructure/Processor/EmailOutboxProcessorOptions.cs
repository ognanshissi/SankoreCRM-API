namespace Sankore.Modules.Notifications.Infrastructure.Processor;

internal sealed class EmailOutboxProcessorOptions
{
    public const string SectionName = "Notifications:OutboxProcessor";

    /// <summary>Number of messages fetched per polling tick.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Seconds between polling ticks.</summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum delivery attempts before a message is moved to DeadLettered.
    /// After the last attempt the message is not retried automatically.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Minutes after which a message left in Sending is considered abandoned and
    /// reclaimed for another attempt. Must exceed the slowest realistic send.
    /// </summary>
    public int StuckSendingTimeoutMinutes { get; set; } = 5;
}
