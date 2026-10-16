namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Missed;
using Xunit;

/// <summary>
/// The window arithmetic of TEL-B-13, which is the whole of the decision.
///
/// <para>
/// Pure cases on purpose: the job around this reads rows and publishes, while the question "is
/// this the same person still trying" is what an agent's follow-up list stands or falls on.
/// </para>
/// </summary>
public sealed class MissedCallGroupingTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    private const string Aya = "aaaa";
    private const string Koffi = "bbbb";

    [Fact]
    public void Three_attempts_in_ten_minutes_are_one_entry()
    {
        var groups = MissedCallGrouping.Group(
        [
            Attempt(Aya, At),
            Attempt(Aya, At.AddMinutes(4)),
            Attempt(Aya, At.AddMinutes(9)),
        ]);

        // Without grouping this is three entries, three notifications and — if the optional lead
        // creation is on — three leads for one person.
        var group = groups.Should().ContainSingle().Subject;

        group.AttemptCount.Should().Be(3);
        group.FirstAttemptAt.Should().Be(At);
        group.LastAttemptAt.Should().Be(At.AddMinutes(9));
    }

    [Fact]
    public void The_group_speaks_for_its_LAST_attempt()
    {
        var lead = Guid.NewGuid();

        var groups = MissedCallGrouping.Group(
        [
            // The first ring was journalled before a lead for that number existed.
            Attempt(Aya, At, callId: Guid.NewGuid()),
            Attempt(Aya, At.AddMinutes(5), callId: Guid.NewGuid(), entityId: lead),
        ]);

        var group = groups.Single();

        // That is the call an agent should open: it carries the most recent reading of who the
        // caller is.
        group.EntityId.Should().Be(lead);
        group.LastAttemptAt.Should().Be(At.AddMinutes(5));
    }

    [Fact]
    public void A_gap_longer_than_the_window_starts_a_new_group()
    {
        var groups = MissedCallGrouping.Group(
        [
            Attempt(Aya, At),
            Attempt(Aya, At.Add(MissedCallGrouping.Window).AddMinutes(1)),
        ]);

        // Somebody who gave up and came back later is a new conversation.
        groups.Should().HaveCount(2);
    }

    [Fact]
    public void The_window_is_measured_between_CONSECUTIVE_attempts()
    {
        // Every twenty minutes for two hours: one person still trying. A fixed window from the
        // FIRST attempt would cut them into four groups at arbitrary points.
        var groups = MissedCallGrouping.Group(
        [
            Attempt(Aya, At),
            Attempt(Aya, At.AddMinutes(20)),
            Attempt(Aya, At.AddMinutes(40)),
            Attempt(Aya, At.AddMinutes(60)),
            Attempt(Aya, At.AddMinutes(80)),
            Attempt(Aya, At.AddMinutes(100)),
        ]);

        groups.Should().ContainSingle().Which.AttemptCount.Should().Be(6);
    }

    [Fact]
    public void Two_callers_are_never_folded_together()
    {
        var groups = MissedCallGrouping.Group(
        [
            Attempt(Aya, At),
            Attempt(Koffi, At.AddMinutes(1)),
        ]);

        groups.Should().HaveCount(2);
        groups.Should().OnlyContain(g => g.AttemptCount == 1);
    }

    [Fact]
    public void Each_withheld_caller_id_is_its_own_group()
    {
        var groups = MissedCallGrouping.Group(
        [
            Attempt(string.Empty, At),
            Attempt(string.Empty, At.AddMinutes(1)),
            Attempt(string.Empty, At.AddMinutes(2)),
        ]);

        // Folding them would report "forty missed calls from one caller" about forty different
        // people.
        groups.Should().HaveCount(3);
    }

    [Fact]
    public void Attempts_arriving_out_of_order_are_grouped_all_the_same()
    {
        // The job reads rows in whatever order the query returns them, and "the gap since the
        // previous attempt" is meaningless on unordered input.
        var groups = MissedCallGrouping.Group(
        [
            Attempt(Aya, At.AddMinutes(9)),
            Attempt(Aya, At),
            Attempt(Aya, At.AddMinutes(4)),
        ]);

        var group = groups.Should().ContainSingle().Subject;

        group.AttemptCount.Should().Be(3);
        group.FirstAttemptAt.Should().Be(At);
        group.LastAttemptAt.Should().Be(At.AddMinutes(9));
    }

    [Fact]
    public void Nothing_in_answers_nothing_out()
    {
        MissedCallGrouping.Group([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(TelCallStatus.Missed, true)]
    [InlineData(TelCallStatus.Voicemail, true)]
    [InlineData(TelCallStatus.Failed, false)]
    [InlineData(TelCallStatus.Completed, false)]
    public void What_counts_as_missed(TelCallStatus status, bool expected)
    {
        // Voicemail IS missed — somebody left a message, which is the clearest possible request
        // for a call back. Failed is NOT: an operator refusal or an invalid number has nobody
        // waiting, and putting those in a follow-up list fills it with work that cannot be done.
        MissedCallStatuses.IsMissed(status).Should().Be(expected);
    }

    private static MissedCallAttempt Attempt(
        string caller, DateTimeOffset at, Guid? callId = null, Guid? entityId = null)
        => new(
            callId ?? Guid.NewGuid(),
            caller,
            at,
            entityId is null ? null : TelEntityTypes.Lead,
            entityId,
            CrmUserId: null);
}
