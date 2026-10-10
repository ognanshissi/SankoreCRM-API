namespace Sankore.Integration.RelayAgent.Observability;

using System.Collections.Concurrent;
using Sankore.Integration.RelayAgent.Protocol;

/// <summary>
/// What the heartbeat reports about each relayed system (criterion 5: "its version, its state and
/// the latency towards each connected system").
///
/// <para>
/// Fed from two sources, and the distinction is the whole design. An executed order is the
/// authoritative measurement — it is the real latency of real work — but an agent can sit idle
/// for hours, and a heartbeat that reported nothing for an idle target would leave an operator
/// unable to tell "nobody asked" from "the CBS is down". So a cheap TCP-connect probe
/// (<see cref="TargetProbe"/>) fills the gaps, and whichever observation is MORE RECENT wins.
/// </para>
///
/// <para>
/// In memory only, keyed by the target names from the configuration file, reset by a restart.
/// Nothing here is persisted, which is criterion 4 applied to telemetry as well as to payloads:
/// the platform is where a history of heartbeats belongs, because the platform is ours.
/// </para>
/// </summary>
public sealed class TargetHealthRegistry
{
    private readonly ConcurrentDictionary<string, Observation> _observations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _clock;

    public TargetHealthRegistry(TimeProvider clock) => _clock = clock;

    /// <summary>
    /// Records what an executed order observed. Called for every order, success or failure: a
    /// failure is a measurement too, and the one an operator most needs.
    /// </summary>
    public void RecordOrder(string target, bool reached, long latencyMs)
        => Record(target, reached ? RelayTargetState.Reachable : RelayTargetState.Unreachable, latencyMs);

    /// <summary>Records what a probe observed.</summary>
    public void RecordProbe(string target, bool reached, long latencyMs)
        => Record(target, reached ? RelayTargetState.Reachable : RelayTargetState.Unreachable, latencyMs);

    private void Record(string target, RelayTargetState state, long latencyMs)
    {
        var at = _clock.GetUtcNow();
        _observations[target] = new Observation(state, latencyMs, at);
    }

    /// <summary>
    /// The heartbeat's view. Driven by the DECLARED target list and not by what has been
    /// observed, so a target configured and never reached is reported as
    /// <see cref="RelayTargetState.Unknown"/> rather than being absent — an absent line in a
    /// dashboard is read as "fine".
    /// </summary>
    public IReadOnlyList<RelayTargetHealth> Snapshot(
        IEnumerable<(string Name, RelayOrderKind Kind)> declaredTargets)
    {
        ArgumentNullException.ThrowIfNull(declaredTargets);

        var health = new List<RelayTargetHealth>();

        foreach (var (name, kind) in declaredTargets)
        {
            health.Add(_observations.TryGetValue(name, out var observation)
                ? new RelayTargetHealth(name, kind, observation.State, observation.LatencyMs, observation.At)
                : new RelayTargetHealth(name, kind, RelayTargetState.Unknown, null, null));
        }

        return health;
    }

    /// <summary>
    /// Degraded as soon as ONE declared target is unreachable, and Unknown does not count.
    ///
    /// <para>
    /// Unknown deliberately does not degrade the agent: on the first heartbeat after a start
    /// every target is Unknown, and an agent that announced itself Degraded for its first thirty
    /// seconds would teach its operator to ignore the state field.
    /// </para>
    /// </summary>
    public RelayAgentState OverallState(
        IEnumerable<(string Name, RelayOrderKind Kind)> declaredTargets)
        => Snapshot(declaredTargets).Any(t => t.State == RelayTargetState.Unreachable)
            ? RelayAgentState.Degraded
            : RelayAgentState.Healthy;

    private sealed record Observation(RelayTargetState State, long LatencyMs, DateTimeOffset At);
}
