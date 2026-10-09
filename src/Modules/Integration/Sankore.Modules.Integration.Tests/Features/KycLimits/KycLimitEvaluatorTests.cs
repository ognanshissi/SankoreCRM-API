namespace Sankore.Modules.Integration.Tests.Features.KycLimits;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.KycLimits;
using Xunit;

/// <summary>
/// INT-22 criteria 1 to 3, at the level where the decision is actually made: which crossings come
/// out of a pair of figures and the ceilings M02 hands back.
/// </summary>
public sealed class KycLimitEvaluatorTests
{
    [Fact]
    public void At_the_alert_threshold_the_balance_ceiling_is_approaching()
    {
        // 80 % of 250 000, both of them M02's factory defaults. Inclusive: "at the alert
        // threshold" is where the agent is warned.
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 200_000m, monthlyFlow: 0m, KycLimitsTestContext.Capped());

        crossings.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Kind = KycLimitKind.Balance,
                Severity = KycLimitSeverity.Approaching,
                Observed = 200_000m,
                Ceiling = 250_000m,
            });
    }

    [Fact]
    public void Below_the_alert_threshold_nothing_is_raised()
    {
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 199_999m, monthlyFlow: 0m, KycLimitsTestContext.Capped());

        crossings.Should().BeEmpty();
    }

    [Fact]
    public void At_the_ceiling_exactly_is_still_only_approaching()
    {
        // MaxBalance is the ceiling on the balance AFTER an operation — the highest value still
        // allowed — so equality is not a breach.
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 250_000m, monthlyFlow: 0m, KycLimitsTestContext.Capped());

        crossings.Should().ContainSingle()
            .Which.Severity.Should().Be(KycLimitSeverity.Approaching);
    }

    [Fact]
    public void Past_the_ceiling_exceeded_supersedes_approaching()
    {
        // A customer past its ceiling is also past 80 % of it. Publishing both would hand M02 two
        // upgrade tasks for one customer; the stronger statement is the one that is acted on.
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 250_001m, monthlyFlow: 0m, KycLimitsTestContext.Capped());

        crossings.Should().ContainSingle()
            .Which.Severity.Should().Be(KycLimitSeverity.Exceeded);
    }

    [Fact]
    public void The_two_ceilings_are_watched_independently()
    {
        // Balance comfortably inside its ceiling, flow past its own: criterion 1 names both, and
        // the ledger keys on LimitKind precisely so one does not mask the other.
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 1_000m, monthlyFlow: 600_000m, KycLimitsTestContext.Capped());

        crossings.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Kind = KycLimitKind.Flow,
                Severity = KycLimitSeverity.Exceeded,
                Observed = 600_000m,
                Ceiling = 500_000m,
            });
    }

    [Fact]
    public void Both_ceilings_can_be_crossed_at_once_at_different_severities()
    {
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 300_000m, monthlyFlow: 420_000m, KycLimitsTestContext.Capped());

        crossings.Should().BeEquivalentTo(
        [
            new KycLimitCrossing(
                KycLimitKind.Balance, KycLimitSeverity.Exceeded, 300_000m, 250_000m),
            new KycLimitCrossing(
                KycLimitKind.Flow, KycLimitSeverity.Approaching, 420_000m, 500_000m),
        ]);
    }

    [Fact]
    public void An_uncapped_customer_yields_nothing_whatever_the_figures()
    {
        // Full KYC: no ceiling exists, and the amounts on the record are documented as
        // meaningless. A watch that compared against them would alert the customers already in
        // order.
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 50_000_000m, monthlyFlow: 90_000_000m, KycLimitsTestContext.Uncapped());

        crossings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(50, 500_000, KycLimitSeverity.Approaching)]
    [InlineData(90, 500_000, null)]
    public void The_alert_threshold_comes_from_the_kyc_module_and_not_from_a_literal(
        int alertPct, decimal maxBalance, KycLimitSeverity? expected)
    {
        // Half of a million-franc ceiling. At a 50 % threshold the agent is warned; at 90 % the
        // same figure is unremarkable. Nothing in this module may hold the 80 % default.
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 250_000m,
            monthlyFlow: 0m,
            KycLimitsTestContext.Capped(maxBalance: maxBalance, alertPct: alertPct));

        if (expected is null) crossings.Should().BeEmpty();
        else crossings.Should().ContainSingle().Which.Severity.Should().Be(expected);
    }

    [Fact]
    public void The_ceilings_come_from_the_kyc_module_and_not_from_a_literal()
    {
        // A tenant that raised its balance ceiling to a million: 250 000 — the FACTORY default —
        // is then nowhere near it, and a module holding that figure itself would alert anyway.
        var raised = KycLimitEvaluator.Evaluate(
            totalBalance: 250_000m, monthlyFlow: 0m,
            KycLimitsTestContext.Capped(maxBalance: 1_000_000m));

        // And one that lowered it to 100 000: the same balance is now a breach.
        var lowered = KycLimitEvaluator.Evaluate(
            totalBalance: 250_000m, monthlyFlow: 0m,
            KycLimitsTestContext.Capped(maxBalance: 100_000m));

        raised.Should().BeEmpty();
        lowered.Should().ContainSingle().Which.Severity.Should().Be(KycLimitSeverity.Exceeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_ceiling_is_not_read_as_a_ceiling(decimal maxBalance)
    {
        // A zero ceiling would make every customer holding one franc Exceeded, every month, over
        // what is a misconfiguration rather than a compliance fact. Staying quiet is the
        // conservative reading; the tenant's settings screen is where it gets fixed.
        var crossings = KycLimitEvaluator.Evaluate(
            totalBalance: 1m, monthlyFlow: 0m,
            KycLimitsTestContext.Capped(maxBalance: maxBalance, maxFlow: 0m));

        crossings.Should().BeEmpty();
    }
}
