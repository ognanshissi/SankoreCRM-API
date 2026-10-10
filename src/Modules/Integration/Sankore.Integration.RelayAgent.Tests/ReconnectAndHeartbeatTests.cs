namespace Sankore.Integration.RelayAgent.Tests;

using FluentAssertions;
using Sankore.Integration.RelayAgent.Channel;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;
using Xunit;

/// <summary>
/// Criterion 2's reconnection policy and criterion 5's heartbeat content.
/// </summary>
public sealed class ReconnectAndHeartbeatTests
{
    [Fact]
    public void The_backoff_grows_and_never_passes_its_cap()
    {
        var backoff = new ReconnectBackoff(new RelayReconnectOptions
        {
            InitialDelaySeconds = 1,
            MaxDelaySeconds = 60,
            JitterRatio = 0.25,
        });

        var delays = Enumerable.Range(0, 40).Select(_ => backoff.Next()).ToList();

        // The cap is a cap, jitter included. Without the clamp in Next() a +25% spread would put
        // delays at 75 seconds against a configured ceiling of 60 — which was the first version
        // of this, and this assertion is why it changed.
        delays.Should().OnlyContain(d => d <= TimeSpan.FromSeconds(60));

        // Never sooner than configured either: a jittered first retry must not become a spin.
        delays.Should().OnlyContain(d => d >= TimeSpan.FromSeconds(1));

        // It actually grows: the tail sits at the cap, the head does not.
        delays[0].Should().BeLessThan(TimeSpan.FromSeconds(2));
        delays[^1].Should().BeGreaterThan(TimeSpan.FromSeconds(44));
    }

    [Fact]
    public void A_successful_session_resets_the_backoff()
    {
        var backoff = new ReconnectBackoff(new RelayReconnectOptions
        {
            InitialDelaySeconds = 1,
            MaxDelaySeconds = 60,
            JitterRatio = 0,
        });

        for (var i = 0; i < 10; i++) backoff.Next();
        backoff.Attempt.Should().Be(10);

        backoff.Reset();

        backoff.Attempt.Should().Be(0);
        backoff.Next().Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Jitter_can_be_switched_off_and_then_the_sequence_is_exact()
    {
        var backoff = new ReconnectBackoff(new RelayReconnectOptions
        {
            InitialDelaySeconds = 2,
            MaxDelaySeconds = 16,
            JitterRatio = 0,
        });

        new[] { backoff.Next(), backoff.Next(), backoff.Next(), backoff.Next(), backoff.Next() }
            .Should().Equal(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(16),
                TimeSpan.FromSeconds(16));
    }

    [Fact]
    public void A_declared_but_never_probed_target_is_reported_Unknown_and_not_omitted()
    {
        // An absent line in a dashboard reads as "fine". Unknown is the honest answer, and it
        // deliberately does not degrade the agent — every target is Unknown for the first few
        // seconds after a start.
        var registry = new TargetHealthRegistry(TimeProvider.System);
        var declared = new[] { ("cbs-api", RelayOrderKind.HttpCall) };

        var snapshot = registry.Snapshot(declared);

        snapshot.Should().ContainSingle();
        snapshot[0].State.Should().Be(RelayTargetState.Unknown);
        snapshot[0].LatencyMs.Should().BeNull();
        registry.OverallState(declared).Should().Be(RelayAgentState.Healthy);
    }

    [Fact]
    public void One_unreachable_target_degrades_the_agent_and_carries_its_latency()
    {
        var registry = new TargetHealthRegistry(TimeProvider.System);
        var declared = new[]
        {
            ("cbs-api", RelayOrderKind.HttpCall),
            ("cbs-depot", RelayOrderKind.SftpPut),
        };

        registry.RecordProbe("cbs-api", reached: true, 12);
        registry.RecordProbe("cbs-depot", reached: false, 5000);

        registry.OverallState(declared).Should().Be(RelayAgentState.Degraded);

        var snapshot = registry.Snapshot(declared);
        snapshot.Single(t => t.Name == "cbs-api").LatencyMs.Should().Be(12);
        snapshot.Single(t => t.Name == "cbs-depot").State.Should().Be(RelayTargetState.Unreachable);
    }

    [Fact]
    public void An_executed_order_overrides_an_earlier_probe()
    {
        // A probe only proves a TCP connect. A real order is the authoritative measurement, so
        // whichever observation is more recent wins — here the order.
        var registry = new TargetHealthRegistry(TimeProvider.System);
        var declared = new[] { ("cbs-api", RelayOrderKind.HttpCall) };

        registry.RecordProbe("cbs-api", reached: true, 8);
        registry.RecordOrder("cbs-api", reached: false, 30_000);

        registry.Snapshot(declared)[0].State.Should().Be(RelayTargetState.Unreachable);
        registry.Snapshot(declared)[0].LatencyMs.Should().Be(30_000);
    }

    [Fact]
    public void The_handshake_announces_each_declared_target_under_the_kinds_it_allows()
    {
        // What the platform is told the agent can do. An SFTP target allowing both directions is
        // announced twice, once per order kind, because that is what an order carries.
        var options = new RelayAgentOptions();

        options.HttpTargets.Add(new RelayHttpTargetOptions
        {
            Name = "cbs-api",
            BaseUrl = "https://cbs.lan/api/",
        });

        options.SftpTargets.Add(new RelaySftpTargetOptions
        {
            Name = "cbs-echange",
            Host = "sftp.lan",
            Username = "sankore",
            Password = "x",
            HostKeyFingerprintSha256 = "AAAA",
            RemotePath = "/echange",
            AllowPut = true,
            AllowRead = true,
        });

        options.AllTargets().Should().BeEquivalentTo(new[]
        {
            ("cbs-api", RelayOrderKind.HttpCall),
            ("cbs-echange", RelayOrderKind.SftpPut),
            ("cbs-echange", RelayOrderKind.SftpRead),
        });
    }
}
