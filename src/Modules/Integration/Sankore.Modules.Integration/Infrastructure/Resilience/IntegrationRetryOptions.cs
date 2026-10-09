namespace Sankore.Modules.Integration.Infrastructure.Resilience;

/// <summary>
/// The dispatcher's attempt budget (INT-06, criterion 4), bound from <c>Integration:Retry</c>.
///
/// <para>
/// <b>This is the ONE place the budget lives.</b> Both halves of criterion 4 read it: the delay
/// curve comes from <see cref="ExponentialCommandRetryPolicy"/>, which is built from this object,
/// and the ceiling that turns the last transient failure into a <c>Rejected</c> command comes from
/// <see cref="MaxAttempts"/>, read by <c>DispatchTenantCommandsJob</c>. A second constant anywhere
/// would let the curve and the ceiling drift, and the symptom — a command retried nine times, or
/// rejected on its seventh — is invisible until an operator counts rows.
/// </para>
///
/// <para>
/// Environment variables use the usual double underscore:
/// <c>Integration__Retry__MaxAttempts</c>.
/// </para>
/// </summary>
internal sealed class IntegrationRetryOptions
{
    public const string SectionName = "Integration:Retry";

    /// <summary>
    /// Attempts a command gets before it is rejected. Eight, with the curve below, spans a little
    /// over two hours — long enough to ride out a CBS maintenance window, short enough that an
    /// operator sees the rejection the same morning.
    /// </summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>
    /// Ceiling on a single delay. One hour: past that the retry is no longer a retry, it is a
    /// nightly job, and a human should be looking at the connection instead.
    /// </summary>
    public int CapSeconds { get; set; } = 3600;

    /// <summary>
    /// First delay, doubled at every attempt. Thirty seconds and eight attempts reach the cap
    /// exactly at the last one (30 × 2^7 = 3840 > 3600), so the whole budget is spent inside the
    /// hour-capped curve rather than flattening out three attempts early.
    /// </summary>
    public int BaseSeconds { get; set; } = 30;

    /// <summary>
    /// Fraction of the computed delay that may be shaved off at random, to decorrelate the
    /// retries of a tenant whose commands all failed in the same second. Subtracted and never
    /// added: jitter must not push a delay past <see cref="CapSeconds"/>.
    /// </summary>
    public double JitterFraction { get; set; } = 0.2;
}
