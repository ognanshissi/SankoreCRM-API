namespace Sankore.Modules.Integration.Infrastructure.Resilience;

using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Features.Commands;

/// <summary>
/// The platform's backoff curve: <c>min(cap, base × 2^(attempts-1))</c>, minus jitter
/// (INT-06, criterion 4).
///
/// <para>
/// <b>It is the one place the attempt budget lives.</b> Both halves of the criterion come out of
/// the same <see cref="IntegrationRetryOptions"/>: the curve from
/// <see cref="NextAttemptAt(int)"/>, and the ceiling past which a transient failure becomes a
/// rejection from <see cref="MaxAttempts"/>. <c>ExecuteIntegrationCommandHandler</c> reads both
/// off this interface and compares nothing of its own, so there is no second reader to drift —
/// which is how a deployment ends up retrying eight times in four seconds, or twice over a day.
/// </para>
///
/// <para>
/// It answers a date and not a delay on purpose. <c>IntegrationCommand.ScheduleRetry</c> stores
/// <c>NextAttemptAt</c> and the dispatcher's "is it due" predicate compares against it, so the
/// one thing the retry has to agree on with the rest of the module is an instant.
/// </para>
///
/// <para>
/// <b>Nothing here enqueues a Hangfire job.</b> The reschedule is a column, claimed by the next
/// pass of <see cref="Features.Dispatch.IntegrationDispatchOrchestratorJob"/>. That is the same
/// reason M02's <c>RunKycVerificationHandler</c> schedules with a delay rather than enqueuing:
/// Hangfire's storage is not in this module's transaction, so a job started the instant a handler
/// saved can read the row back in its previous status. Here the problem cannot arise at all — the
/// retry IS the row, and the orchestrator only ever reads committed state.
/// </para>
///
/// <para>
/// <b>Validated at start-up, not clamped.</b> <c>AddDispatchServices</c> binds these options with
/// <c>ValidateOnStart</c> and a non-positive value fails the boot. A clamp would be kinder to the
/// deployment and worse for the operator: a <c>MaxAttempts</c> of 0 silently corrected to 1 is a
/// platform that rejects every command on its first timeout, and nothing in the logs says the
/// configuration was ignored.
/// </para>
///
/// <para>
/// Jitter is subtracted and never added, so a delay never exceeds
/// <see cref="IntegrationRetryOptions.CapSeconds"/>: the cap is a promise to whoever reads the
/// configuration, and a curve that overshoots it by a fifth cannot be reasoned about. Set
/// <c>JitterFraction</c> to 0 when the stored timestamps must be exactly predictable — the
/// decorrelation it buys only matters for a tenant whose commands all failed in the same second.
/// </para>
/// </summary>
internal sealed class ExponentialCommandRetryPolicy(
    IOptions<IntegrationRetryOptions> options,
    TimeProvider clock) : ICommandRetryPolicy
{
    private readonly IntegrationRetryOptions _options = options.Value;

    public int MaxAttempts => _options.MaxAttempts;

    public DateTimeOffset NextAttemptAt(int attempts)
        => clock.GetUtcNow().Add(DelayFor(attempts));

    /// <summary>
    /// The delay on its own, so a test can pin the curve without pinning a clock. Internal
    /// rather than part of <see cref="ICommandRetryPolicy"/>: a caller only ever needs the
    /// instant, and a second member on the interface would be a second thing to implement.
    /// </summary>
    internal TimeSpan DelayFor(int attempts)
    {
        // A count below one means the claim did not happen. Treated as the first attempt rather
        // than as an error, because the one outcome that loses a command is refusing to schedule
        // its retry — and a zero delay would be a hot loop against whatever just failed.
        var step = Math.Max(1, attempts);

        // Bounding the exponent first keeps the arithmetic finite whatever the column holds:
        // 2^(step-1) overflows a TimeSpan long before it overflows a double.
        var exponent = Math.Min(step - 1, 30);
        var seconds = (double)_options.BaseSeconds * Math.Pow(2, exponent);

        var capped = Math.Min(seconds, _options.CapSeconds);

        // A fraction of the capped delay, so the result always lands in
        // [capped × (1 - f), capped] — bounded on both sides, which is what a test can assert.
        var fraction = Math.Clamp(_options.JitterFraction, 0d, 1d);
        var shave = capped * fraction * Random.Shared.NextDouble();

        return TimeSpan.FromSeconds(capped - shave);
    }
}
