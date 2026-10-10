namespace Sankore.Modules.Integration.Tests.Infrastructure.Resilience;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.Dispatch;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Xunit;

/// <summary>
/// INT-06 criterion 4: the 8-attempt, one-hour-capped curve, its configurability, and the boot
/// failure that stops a nonsensical budget reaching production.
/// </summary>
public sealed class ExponentialCommandRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_to_eight_attempts_and_a_one_hour_cap()
    {
        var policy = Build();

        policy.MaxAttempts.Should().Be(8);

        // 30s doubling: 30, 60, 120, 240, 480, 960, 1920, then 3840 clipped to the 3600 cap. The
        // budget spans a little over two hours, which is the shape a CBS maintenance window needs.
        Bounded(policy.DelayFor(1), 30);
        Bounded(policy.DelayFor(2), 60);
        Bounded(policy.DelayFor(3), 120);
        Bounded(policy.DelayFor(4), 240);
        Bounded(policy.DelayFor(5), 480);
        Bounded(policy.DelayFor(6), 960);
        Bounded(policy.DelayFor(7), 1920);
        Bounded(policy.DelayFor(8), 3600);
    }

    [Fact]
    public void Never_exceeds_the_cap_however_many_attempts_are_claimed()
    {
        var policy = Build();

        // Jitter is subtracted and never added, so the cap is a promise and not an average. A
        // ninth attempt cannot happen with MaxAttempts at 8, but the arithmetic must stay finite
        // whatever the column holds.
        foreach (var attempts in new[] { 9, 20, 64, int.MaxValue })
            policy.DelayFor(attempts).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void Never_schedules_a_zero_delay_even_for_an_unclaimed_command()
    {
        var policy = Build();

        // A zero or negative attempt count means the claim did not happen. Refusing to schedule
        // is the one outcome that loses a command, and a zero delay is a hot loop against whatever
        // just failed.
        foreach (var attempts in new[] { 0, -1 })
            policy.DelayFor(attempts).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Is_configurable_end_to_end()
    {
        // JitterFraction 0 makes the curve exact, which is also how a deployment that wants
        // operator-readable NextAttemptAt timestamps turns the randomisation off.
        var policy = Build(new Dictionary<string, string?>
        {
            ["Integration:Retry:MaxAttempts"] = "3",
            ["Integration:Retry:CapSeconds"] = "60",
            ["Integration:Retry:BaseSeconds"] = "5",
            ["Integration:Retry:JitterFraction"] = "0",
        });

        policy.MaxAttempts.Should().Be(3);
        policy.DelayFor(1).Should().Be(TimeSpan.FromSeconds(5));
        policy.DelayFor(2).Should().Be(TimeSpan.FromSeconds(10));
        policy.DelayFor(3).Should().Be(TimeSpan.FromSeconds(20));
        policy.DelayFor(9).Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Answers_an_instant_derived_from_the_clock()
    {
        var policy = Build(new Dictionary<string, string?>
        {
            ["Integration:Retry:BaseSeconds"] = "90",
            ["Integration:Retry:JitterFraction"] = "0",
        });

        // An instant and not a duration, because IntegrationCommand stores NextAttemptAt and the
        // dispatcher's due predicate compares against it.
        policy.NextAttemptAt(1).Should().Be(Now.AddSeconds(90));
    }

    [Theory]
    [InlineData("MaxAttempts", "0")]
    [InlineData("MaxAttempts", "-1")]
    [InlineData("CapSeconds", "0")]
    [InlineData("BaseSeconds", "0")]
    [InlineData("JitterFraction", "1")]
    public void A_nonsensical_budget_fails_the_boot(string key, string value)
    {
        var provider = Container(new Dictionary<string, string?>
        {
            [$"Integration:Retry:{key}"] = value,
        });

        // ValidateOnStart runs through IStartupValidator; resolving the options is what trips it,
        // and a 0 silently clamped to 1 would be a platform that rejects every command on its
        // first timeout with nothing in the logs to say so.
        var resolve = () => provider.GetRequiredService<IOptions<IntegrationRetryOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>()
            .WithMessage($"*Integration:Retry:{key}*");
    }

    [Fact]
    public void The_registered_policy_is_the_resilience_one()
    {
        var provider = Container([]);

        // ExecuteIntegrationCommandHandler takes ICommandRetryPolicy and compares nothing of its
        // own, so this registration IS the platform's budget. Pinned here: a container that
        // resolved some other implementation would change the curve and the ceiling together,
        // with nothing in the logs to say so.
        provider.GetRequiredService<ICommandRetryPolicy>()
            .Should().BeOfType<ExponentialCommandRetryPolicy>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ExponentialCommandRetryPolicy Build(Dictionary<string, string?>? settings = null)
    {
        var options = Container(settings ?? [])
            .GetRequiredService<IOptions<IntegrationRetryOptions>>();

        return new ExponentialCommandRetryPolicy(options, new FixedClock(Now));
    }

    private static ServiceProvider Container(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddDispatchServices(config);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Jitter shaves up to 20% off, so an expectation is a band and not a value: the delay lands
    /// in [expected × 0.8, expected].
    /// </summary>
    private static void Bounded(TimeSpan actual, double expectedSeconds)
    {
        actual.TotalSeconds.Should().BeLessThanOrEqualTo(expectedSeconds);
        actual.TotalSeconds.Should().BeGreaterThanOrEqualTo(expectedSeconds * 0.8);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
