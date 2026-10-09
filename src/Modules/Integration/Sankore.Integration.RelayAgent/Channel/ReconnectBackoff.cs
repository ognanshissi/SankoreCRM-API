namespace Sankore.Integration.RelayAgent.Channel;

using Sankore.Integration.RelayAgent.Configuration;

/// <summary>
/// The reconnection delay (criterion 2: "with automatic reconnection").
///
/// <para>
/// Exponential, jittered, and <b>capped</b>. The cap is the part that exists for somebody else's
/// benefit: when SANKORE goes down, every enrolled agent of every IMF notices within a second,
/// and an uncapped-rate loop would then put a steady synchronised load on a platform that is
/// already in an incident. The agents would be a second incident sitting on top of the first, at
/// the worst possible moment. One attempt a minute per agent is enough to recover within a minute
/// of the platform returning, and small enough to be invisible while it is away.
/// </para>
///
/// <para>
/// The jitter is a separate concern from the backoff and solves a different problem. Without it,
/// agents that lost the same platform at the same instant retry at the same instant for ever —
/// the cap would then shape a herd into a once-a-minute spike rather than dissolving it. With
/// ±25% the attempts spread across a 30-second band.
/// </para>
///
/// <para>
/// Not a Polly pipeline: Polly's retry wraps an operation, and what is being retried here is the
/// whole lifetime of a session — connect, handshake, serve for hours, lose it. There is no
/// operation to wrap. <see cref="Random.Shared"/> rather than a seeded instance because the
/// spread wants to differ between two agents started from the same image at the same moment,
/// which is exactly when a fixed seed would fail.
/// </para>
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly double _jitterRatio;

    private int _attempt;

    public ReconnectBackoff(RelayReconnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _initial = TimeSpan.FromSeconds(options.InitialDelaySeconds);
        _max = TimeSpan.FromSeconds(options.MaxDelaySeconds);
        _jitterRatio = options.JitterRatio;
    }

    /// <summary>How many consecutive failures since the last successful session.</summary>
    public int Attempt => _attempt;

    /// <summary>
    /// Called once a session has been established. Not when the dial succeeds — when the protocol
    /// handshake does: a platform that accepts the TCP connection and then rejects the
    /// certificate would otherwise reset the backoff on every attempt and hammer at the initial
    /// delay for ever.
    /// </summary>
    public void Reset() => _attempt = 0;

    /// <summary>Delay before the next attempt, and counts the attempt.</summary>
    public TimeSpan Next()
    {
        // Doubling computed in seconds and clamped before it is turned into a TimeSpan: at
        // attempt 40 the shift alone would overflow, and an overflowed delay is an agent that
        // never comes back.
        var exponent = Math.Min(_attempt, 16);
        var seconds = Math.Min(_initial.TotalSeconds * Math.Pow(2, exponent), _max.TotalSeconds);

        _attempt++;

        if (_jitterRatio <= 0) return TimeSpan.FromSeconds(seconds);

        var spread = seconds * _jitterRatio;
        var jittered = seconds + ((Random.Shared.NextDouble() * 2 - 1) * spread);

        // Clamped on both sides, and both sides matter.
        //
        // Below: never sooner than the initial delay. With jitter at its lowest the first retry
        // would otherwise beat the configured delay, and "retry almost immediately" is how a
        // connect loop becomes a spin loop against a platform that refuses at the TLS layer.
        //
        // Above: never past the cap, so the cap is a cap. Once the backoff has settled there the
        // spread becomes one-sided — delays fall in [max·(1−ratio), max] rather than straddling
        // it — which still dissolves a herd, and that is the job jitter was brought in for.
        var bounded = Math.Clamp(jittered, _initial.TotalSeconds, _max.TotalSeconds);

        return TimeSpan.FromSeconds(bounded);
    }
}
