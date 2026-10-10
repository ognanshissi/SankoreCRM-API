namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using FluentAssertions;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Xunit;

/// <summary>
/// The cut-off arithmetic of criterion 2, pinned on its own. "At the cut-off and not before" is
/// one comparison, and these are the cases where getting it wrong sends a day's customer writes
/// twelve hours early or a day late.
/// </summary>
public sealed class OutboundBatchCycleTests
{
    private static readonly TimeOnly SixPm = new(18, 0);

    [Fact]
    public void Before_the_cut_off_the_current_cycle_is_yesterdays()
    {
        var now = new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

        OutboundBatchCycle.CurrentCutOff(now, SixPm)
            .Should().Be(new DateTimeOffset(2026, 3, 10, 18, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void At_the_cut_off_to_the_second_the_cycle_is_todays()
    {
        var now = new DateTimeOffset(2026, 3, 11, 18, 0, 0, TimeSpan.Zero);

        OutboundBatchCycle.CurrentCutOff(now, SixPm)
            .Should().Be(now, "the cut-off instant itself belongs to the cycle it opens");
    }

    [Fact]
    public void After_the_cut_off_the_cycle_is_todays()
    {
        var now = new DateTimeOffset(2026, 3, 11, 23, 59, 0, TimeSpan.Zero);

        OutboundBatchCycle.CurrentCutOff(now, SixPm)
            .Should().Be(new DateTimeOffset(2026, 3, 11, 18, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_non_utc_offset_is_normalised_rather_than_read_as_local()
    {
        // 21:00 at UTC+4 is 17:00 UTC, which is BEFORE the 18:00 cut-off. Reading the offset as
        // if it were local would place it after, and the file would leave a day early.
        var now = new DateTimeOffset(2026, 3, 11, 21, 0, 0, TimeSpan.FromHours(4));

        OutboundBatchCycle.CurrentCutOff(now, SixPm)
            .Should().Be(new DateTimeOffset(2026, 3, 10, 18, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_next_cut_off_after_an_instant_is_strictly_after_it()
    {
        var atTheCutOff = new DateTimeOffset(2026, 3, 11, 18, 0, 0, TimeSpan.Zero);

        // Strictly after, so a command created exactly at the cut-off is told about TOMORROW's
        // file rather than about the one it has just missed.
        OutboundBatchCycle.NextCutOffAfter(atTheCutOff, SixPm)
            .Should().Be(new DateTimeOffset(2026, 3, 12, 18, 0, 0, TimeSpan.Zero));

        OutboundBatchCycle.NextCutOffAfter(
                new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero), SixPm)
            .Should().Be(atTheCutOff);
    }

    [Fact]
    public void Eligibility_is_creation_at_or_before_the_cycles_cut_off()
    {
        var cutOff = new DateTimeOffset(2026, 3, 11, 18, 0, 0, TimeSpan.Zero);

        OutboundBatchCycle.IsEligible(cutOff.AddMinutes(-1), cutOff).Should().BeTrue();
        OutboundBatchCycle.IsEligible(cutOff, cutOff).Should().BeTrue();
        OutboundBatchCycle.IsEligible(cutOff.AddSeconds(1), cutOff).Should().BeFalse(
            "a command created after the cut-off leaves with the next file, not this one");
    }
}
