namespace Sankore.Modules.Integration.Features.Commands;

/// <summary>
/// When a transient failure comes back, and how many times it may (INT-05/INT-06).
///
/// <para>
/// An interface and not a static helper because the dispatcher owns the schedule: it is the one
/// that knows how often it scans and therefore what a "due" command costs. The implementation
/// lives with it — <c>Infrastructure/Resilience/ExponentialCommandRetryPolicy</c>, registered by
/// <c>AddDispatchServices</c>, which binds <c>Integration:Retry:*</c> with <c>ValidateOnStart</c>
/// so a nonsensical budget fails the boot instead of being silently clamped. The contract stays
/// here, next to the handler that consumes it.
/// </para>
///
/// <para>
/// <see cref="MaxAttempts"/> is on the same interface as the curve, deliberately: "how long do we
/// keep trying" and "how far apart" are one policy, and
/// <c>ExecuteIntegrationCommandHandler</c> reads both off this type and compares nothing of its
/// own. Two readers of two configuration keys is how a deployment ends up retrying eight times in
/// four seconds, or twice over a day.
/// </para>
/// </summary>
internal interface ICommandRetryPolicy
{
    /// <summary>Attempts a command gets before a transient failure becomes a rejection.</summary>
    int MaxAttempts { get; }

    /// <summary>
    /// When the command becomes due again, given the number of attempts ALREADY consumed
    /// (<c>IntegrationCommand.Attempts</c>, which the claim incremented before the call).
    /// </summary>
    DateTimeOffset NextAttemptAt(int attempts);
}
