namespace Sankore.Shared.Infrastructure.Tests.Compliance;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Shared.Infrastructure.Compliance;
using Sankore.Shared.Kernel.Compliance;
using Xunit;

/// <summary>
/// The CIMA professional-card requirement (articles 503 and 510), which is one rule read by two
/// call sites with deliberately OPPOSITE failure postures. That asymmetry is what this suite
/// mostly pins: it is the kind of thing a later reader "tidies up" into one behaviour, and either
/// direction of tidying is a real fault — a permissive subscription sends a contract out under an
/// unlicensed signature, and a fail-closed dispatch empties every insurance pool during an M12
/// outage while the SLA clock runs.
/// </summary>
public sealed class InsuranceDistributionGuardTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly Guid InsuranceLine = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 11);

    private static readonly DistributionEligibilitySnapshot Licensed =
        new(DistributionEligibility.Valid, new DateOnly(2030, 1, 1), true, 48);

    private static readonly DistributionEligibilitySnapshot Lapsed =
        new(DistributionEligibility.Expired, new DateOnly(2025, 1, 1), true, 48);

    private readonly IDistributionComplianceProvider _compliance =
        Substitute.For<IDistributionComplianceProvider>();

    private InsuranceDistributionGuard Guard() => new(
        _compliance,
        new FixedClock(new DateTimeOffset(Today, TimeOnly.MinValue, TimeSpan.Zero)),
        NullLogger<InsuranceDistributionGuard>.Instance);

    private void LineIsInsurance(bool yes = true)
        => _compliance.IsInsuranceProductLineAsync(
                Tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(yes);

    private void AgentIs(DistributionEligibilitySnapshot snapshot)
    {
        _compliance.GetDistributionEligibilityAsync(
                Tenant, Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(snapshot);

        _compliance.GetDistributionEligibilitiesAsync(
                Tenant, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<Guid, DistributionEligibilitySnapshot>)
                call.ArgAt<IReadOnlyCollection<Guid>>(1).ToDictionary(id => id, _ => snapshot));
    }

    // ── which lines require a card ──────────────────────────────────────────

    [Fact]
    public async Task A_lead_with_no_resolved_product_line_needs_no_card()
    {
        // The ordinary state of a capture. Requiring a card here would turn the licence rule into
        // a conversion blocker on every lead that has not been qualified yet — and would not even
        // ask M12, so the guard answers before any lookup.
        var required = await Guard().RequiresCredentialAsync(Tenant, null, CancellationToken.None);

        required.Should().BeFalse();
        await _compliance.DidNotReceiveWithAnyArgs()
            .IsInsuranceProductLineAsync(default, default, default);
    }

    [Fact]
    public async Task A_non_insurance_line_needs_no_card()
    {
        LineIsInsurance(false);

        (await Guard().RequiresCredentialAsync(Tenant, InsuranceLine, CancellationToken.None))
            .Should().BeFalse();
    }

    // ── the subscription gate: fails the write, fails CLOSED ────────────────

    [Fact]
    public async Task A_licensed_agent_may_sign_an_insurance_subscription()
    {
        LineIsInsurance();
        AgentIs(Licensed);

        var result = await Guard().EnsureMayDistributeAsync(
            Tenant, Agent, InsuranceLine, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_unlicensed_agent_may_not()
    {
        LineIsInsurance();
        AgentIs(DistributionEligibilitySnapshot.NotRecorded);

        var result = await Guard().EnsureMayDistributeAsync(
            Tenant, Agent, InsuranceLine, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(InsuranceDistributionGuard.CredentialRequired);
    }

    [Fact]
    public async Task A_lapsed_card_may_not_either()
    {
        LineIsInsurance();
        AgentIs(Lapsed);

        (await Guard().EnsureMayDistributeAsync(Tenant, Agent, InsuranceLine, CancellationToken.None))
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task An_unlicensed_agent_may_still_sign_a_savings_subscription()
    {
        // The licence governs insurance. Gating everything would make a professional card a
        // precondition for opening a passbook, which no text asks for.
        LineIsInsurance(false);
        AgentIs(DistributionEligibilitySnapshot.NotRecorded);

        (await Guard().EnsureMayDistributeAsync(Tenant, Agent, InsuranceLine, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_outage_reading_the_credential_REFUSES_the_subscription()
    {
        // The posture that matters. This is the last code before a contract leaves the
        // institution, and "M12 was unreachable" is not evidence that the signatory is licensed.
        LineIsInsurance();
        _compliance.GetDistributionEligibilityAsync(
                Tenant, Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("M12 is down"));

        var result = await Guard().EnsureMayDistributeAsync(
            Tenant, Agent, InsuranceLine, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(InsuranceDistributionGuard.CredentialRequired);
    }

    [Fact]
    public async Task An_outage_resolving_the_LINE_also_refuses_the_subscription()
    {
        // Not knowing whether a line is insurance is not a reason to let a contract through.
        _compliance.IsInsuranceProductLineAsync(
                Tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("catalogue unreachable"));

        (await Guard().EnsureMayDistributeAsync(Tenant, Agent, InsuranceLine, CancellationToken.None))
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_cancellation_propagates_rather_than_reading_as_a_refusal()
    {
        // The host shutting down must not be recorded as "this agent is unlicensed".
        LineIsInsurance();
        _compliance.GetDistributionEligibilityAsync(
                Tenant, Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await Guard().EnsureMayDistributeAsync(
            Tenant, Agent, InsuranceLine, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── the dispatch filter: narrows the pool, fails OPEN ───────────────────

    [Fact]
    public async Task The_pool_is_narrowed_to_licensed_agents_on_an_insurance_line()
    {
        LineIsInsurance();

        var licensed = Guid.NewGuid();
        var unlicensed = Guid.NewGuid();

        _compliance.GetDistributionEligibilitiesAsync(
                Tenant, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DistributionEligibilitySnapshot>
            {
                [licensed] = Licensed,
                [unlicensed] = DistributionEligibilitySnapshot.NotRecorded,
            });

        var pool = await Guard().FilterEligibleAsync(
            Tenant, [licensed, unlicensed], InsuranceLine, CancellationToken.None);

        pool.Should().BeEquivalentTo([licensed]);
    }

    [Fact]
    public async Task An_agent_the_provider_says_nothing_about_is_excluded()
    {
        // The contract promises every id asked about is present in the answer. If one is not,
        // the safe reading on a line that requires a card is "not licensed" — a silently absent
        // key must not become a pass.
        LineIsInsurance();

        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();

        _compliance.GetDistributionEligibilitiesAsync(
                Tenant, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DistributionEligibilitySnapshot>
            {
                [known] = Licensed,
            });

        var pool = await Guard().FilterEligibleAsync(
            Tenant, [known, unknown], InsuranceLine, CancellationToken.None);

        pool.Should().BeEquivalentTo([known]);
    }

    [Fact]
    public async Task A_non_insurance_line_returns_the_pool_untouched_and_asks_nothing()
    {
        // The overwhelming majority of dispatches. One cached catalogue read and no credential
        // query at all — the guard has to be free on the path it does not govern.
        LineIsInsurance(false);

        var pool = new[] { Guid.NewGuid(), Guid.NewGuid() };

        (await Guard().FilterEligibleAsync(Tenant, pool, InsuranceLine, CancellationToken.None))
            .Should().BeEquivalentTo(pool);

        await _compliance.DidNotReceiveWithAnyArgs()
            .GetDistributionEligibilitiesAsync(default, default!, default, default);
    }

    [Fact]
    public async Task An_outage_LEAVES_THE_POOL_UNCHANGED()
    {
        // The opposite posture to the subscription gate, and deliberately so: emptying every
        // insurance pool because a credential lookup failed would park every lead on
        // NO_AGENT_AVAILABLE while the SLA clock runs — and no contract has left the institution
        // yet, because the subscription gate still refuses at the end of that road.
        LineIsInsurance();
        _compliance.GetDistributionEligibilitiesAsync(
                Tenant, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("M12 is down"));

        var pool = new[] { Guid.NewGuid(), Guid.NewGuid() };

        (await Guard().FilterEligibleAsync(Tenant, pool, InsuranceLine, CancellationToken.None))
            .Should().BeEquivalentTo(pool);
    }

    [Fact]
    public async Task An_empty_pool_asks_nothing()
    {
        (await Guard().FilterEligibleAsync(Tenant, [], InsuranceLine, CancellationToken.None))
            .Should().BeEmpty();

        await _compliance.DidNotReceiveWithAnyArgs()
            .IsInsuranceProductLineAsync(default, default, default);
    }

    [Fact]
    public async Task The_eligibility_is_asked_AS_OF_today()
    {
        // The contract takes an explicit date because the question is historical as often as it
        // is current. Both call sites must ask about today — a guard that asked about a fixed
        // date would license an agent for ever.
        LineIsInsurance();
        AgentIs(Licensed);

        await Guard().EnsureMayDistributeAsync(Tenant, Agent, InsuranceLine, CancellationToken.None);

        await _compliance.Received(1).GetDistributionEligibilityAsync(
            Tenant, Agent, Today, Arg.Any<CancellationToken>());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
