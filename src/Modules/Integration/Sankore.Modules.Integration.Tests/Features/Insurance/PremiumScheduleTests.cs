namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Features.Insurance.Premiums;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// ASS-08's schedule, as a pure function over dates.
///
/// <para>
/// It is a pure function precisely so these cases exist: the day-of-month rule and the leap-year
/// edge are the kind of thing nobody looks at twice inside a nightly job, and getting one wrong
/// moves every instalment of a policy for ever.
/// </para>
/// </summary>
public sealed class PremiumScheduleTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);

    [Fact]
    public void The_FIRST_instalment_falls_on_the_effective_date_itself()
    {
        // A policy that starts today owes its first premium today. A schedule beginning one
        // period later would give the customer a free month of cover.
        var dates = PremiumSchedule.DueDates(
            Start, Start.AddMonths(2), PremiumPeriodicity.Monthly);

        dates.Should().StartWith(Start);
    }

    [Theory]
    [InlineData(PremiumPeriodicity.Monthly, 13)]
    [InlineData(PremiumPeriodicity.Quarterly, 5)]
    [InlineData(PremiumPeriodicity.SemiAnnual, 3)]
    [InlineData(PremiumPeriodicity.Annual, 2)]
    public void A_year_of_horizon_yields_the_periodicity_s_own_count(
        PremiumPeriodicity periodicity, int expected)
    {
        // Thirteen monthly instalments over twelve months, not twelve: the first falls on the
        // effective date and the thirteenth on its first anniversary, which is inside an
        // inclusive horizon.
        var dates = PremiumSchedule.DueDates(Start, Start.AddMonths(12), periodicity);

        dates.Should().HaveCount(expected);
    }

    [Fact]
    public void A_single_premium_yields_exactly_one_date_whatever_the_horizon()
    {
        // A one-off premium has no schedule. Generating one would debit it again every year.
        var dates = PremiumSchedule.DueDates(
            Start, Start.AddYears(10), PremiumPeriodicity.Single);

        dates.Should().Equal(Start);
    }

    [Fact]
    public void Every_instalment_is_anchored_to_the_effective_date_and_not_to_its_predecessor()
    {
        // The trap this guards: adding one month twelve times to 31 January walks to the 28th and
        // STAYS there, so a policy anchored on the 31st would silently move to the 28th for ever.
        var dates = PremiumSchedule.DueDates(
            new DateOnly(2026, 1, 31), new DateOnly(2027, 1, 31), PremiumPeriodicity.Monthly);

        // February is short, so that instalment falls on the 28th...
        dates.Should().Contain(new DateOnly(2026, 2, 28));

        // ...and March is back on the 31st, which is what anchoring buys.
        dates.Should().Contain(new DateOnly(2026, 3, 31));
        dates.Should().Contain(new DateOnly(2027, 1, 31));
    }

    [Fact]
    public void A_leap_day_anchor_lands_on_the_29th_when_the_year_has_one()
    {
        var dates = PremiumSchedule.DueDates(
            new DateOnly(2024, 2, 29), new DateOnly(2028, 2, 29), PremiumPeriodicity.Annual);

        dates.Should().Contain(new DateOnly(2024, 2, 29));
        dates.Should().Contain(new DateOnly(2025, 2, 28), "2025 is not a leap year");
        dates.Should().Contain(new DateOnly(2028, 2, 29));
    }

    [Fact]
    public void The_schedule_stops_at_the_policy_s_EXPIRY()
    {
        // Running past it would debit a premium for cover the customer no longer has.
        var dates = PremiumSchedule.DueDates(
            Start,
            until: Start.AddMonths(12),
            periodicity: PremiumPeriodicity.Monthly,
            expiryDate: Start.AddMonths(3));

        dates.Should().HaveCount(4, "the effective date plus three monthly instalments");
        dates.Should().AllSatisfy(d => d.Should().BeOnOrBefore(Start.AddMonths(3)));
    }

    [Fact]
    public void A_horizon_before_the_start_yields_nothing()
    {
        PremiumSchedule.DueDates(Start, Start.AddDays(-1), PremiumPeriodicity.Monthly)
            .Should().BeEmpty();
    }

    [Fact]
    public void An_expiry_before_the_start_yields_nothing()
    {
        // A policy whose expiry precedes its effective date is a data fault at the insurer; the
        // generator refuses to invent a schedule for it rather than producing one instalment.
        PremiumSchedule.DueDates(
                Start, Start.AddYears(1), PremiumPeriodicity.Monthly,
                expiryDate: Start.AddDays(-1))
            .Should().BeEmpty();
    }

    [Fact]
    public void Running_the_generator_twice_produces_the_same_dates()
    {
        // The property the unique index depends on. Computing from the policy's own effective date
        // rather than from "today plus n" is what makes a re-run after an interrupted night a
        // no-op instead of a second schedule.
        var first = PremiumSchedule.DueDates(
            Start, Start.AddMonths(12), PremiumPeriodicity.Quarterly);

        var second = PremiumSchedule.DueDates(
            Start, Start.AddMonths(12), PremiumPeriodicity.Quarterly);

        second.Should().Equal(first);
    }

    [Fact]
    public void Every_declared_periodicity_is_mapped()
    {
        // A periodicity the switch does not know would generate ONE instalment and stop, which
        // reads as a paid-up policy. The switch throws instead, and this case is what makes
        // adding a value to the enum a failure here rather than a silent schedule.
        foreach (var periodicity in Enum.GetValues<PremiumPeriodicity>())
        {
            var act = () => PremiumSchedule.MonthsBetweenInstalments(periodicity);
            act.Should().NotThrow($"{periodicity} must be mapped");
        }
    }
}
