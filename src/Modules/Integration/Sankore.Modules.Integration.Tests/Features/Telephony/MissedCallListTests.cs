namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Missed;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The agent's follow-up list (TEL-B-13) — which calls reach it, and what it refuses to say.
/// </summary>
public sealed class MissedCallListTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public MissedCallListTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task My_missed_call_is_listed_with_no_name_and_no_number()
    {
        await SeedAsync("+22507111111", TelCallStatus.Missed, _agent, Now.AddMinutes(-10));

        var list = await ReadAsync();

        var entry = list.Should().ContainSingle().Subject;

        entry.AttemptCount.Should().Be(1);
        entry.CanCreateLead.Should().BeTrue();

        // A name needs the agency perimeter, which this list does not apply — the screen reads
        // GET calls/{callId}/context for that, which does. And no number, ever.
        entry.GetType().GetProperties().Select(p => p.Name)
            .Should().NotContain("DisplayName").And.NotContain("PhoneNumber");
    }

    [Fact]
    public async Task A_call_attributed_to_NOBODY_is_listed_too()
    {
        await SeedAsync("+22507111111", TelCallStatus.Missed, crmUserId: null, Now.AddMinutes(-5));

        // The agency's "to attribute" queue — a call from an unlinked provider account, which is
        // exactly the call most likely to be forgotten.
        (await ReadAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_colleagues_missed_call_is_not_mine_to_see()
    {
        await SeedAsync(
            "+22507111111", TelCallStatus.Missed, Guid.NewGuid(), Now.AddMinutes(-5));

        // Showing it would have two agents calling one person back.
        (await ReadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Voicemail_is_listed_and_a_FAILED_call_is_not()
    {
        await SeedAsync("+22507111111", TelCallStatus.Voicemail, _agent, Now.AddMinutes(-20));
        await SeedAsync("+22507222222", TelCallStatus.Failed, _agent, Now.AddMinutes(-20));

        // Somebody left a message, which is the clearest possible request for a call back. A
        // failed call is an operator refusal or an invalid number — nobody is waiting.
        (await ReadAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_OUTBOUND_unanswered_call_is_not_a_missed_call()
    {
        await SeedAsync(
            "+22507111111", TelCallStatus.Missed, _agent, Now.AddMinutes(-5),
            direction: TelCallDirection.Outbound);

        // The agent dialled and nobody picked up. That is their own call to make again, not
        // somebody waiting to be called back.
        (await ReadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_call_already_wrapped_up_leaves_the_list()
    {
        var call = await SeedAsync(
            "+22507111111", TelCallStatus.Missed, _agent, Now.AddMinutes(-30));

        await using (var db = _factory.CreateContext())
        {
            var row = await db.TelCalls.AsTracking()
                .FirstAsync(c => c.Id == call, CancellationToken.None);

            row.WrapUp("NOT_REACHED", noteEnc: null, Now).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // An agent who called back and noted the outcome must not keep seeing it.
        (await ReadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Three_attempts_from_one_caller_are_one_entry_pointing_at_the_last()
    {
        await SeedAsync("+22507111111", TelCallStatus.Missed, _agent, Now.AddMinutes(-25));
        await SeedAsync("+22507111111", TelCallStatus.Missed, _agent, Now.AddMinutes(-20));
        var last = await SeedAsync(
            "+22507111111", TelCallStatus.Missed, _agent, Now.AddMinutes(-15));

        var entry = (await ReadAsync()).Should().ContainSingle().Subject;

        entry.AttemptCount.Should().Be(3);
        entry.CallId.Should().Be(last);
    }

    [Fact]
    public async Task An_attached_caller_is_named_by_reference_and_offers_no_lead()
    {
        var leadId = Guid.NewGuid();

        await SeedAsync(
            "+22507111111", TelCallStatus.Missed, _agent, Now.AddMinutes(-5), entityId: leadId);

        var entry = (await ReadAsync()).Should().ContainSingle().Subject;

        entry.EntityType.Should().Be(TelEntityTypes.Lead);
        entry.EntityId.Should().Be(leadId);

        // They are already a lead. Offering to create one would be offering to create a duplicate.
        entry.CanCreateLead.Should().BeFalse();
    }

    [Fact]
    public async Task A_call_older_than_the_lookback_is_not_todays_work()
    {
        await SeedAsync("+22507111111", TelCallStatus.Missed, _agent, Now.AddDays(-30));

        (await ReadAsync()).Should().BeEmpty();

        // And it is reachable when asked for explicitly.
        (await ReadAsync(since: Now.AddDays(-60))).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_newest_attempt_comes_first()
    {
        await SeedAsync("+22507111111", TelCallStatus.Missed, _agent, Now.AddHours(-5));
        await SeedAsync("+22507222222", TelCallStatus.Missed, _agent, Now.AddMinutes(-5));

        var list = await ReadAsync();

        // The list is read top-down and the freshest call is the one most likely to be picked up.
        list[0].LastAttemptAt.Should().BeAfter(list[1].LastAttemptAt);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task<Guid> SeedAsync(
        string fromNumber,
        TelCallStatus status,
        Guid? crmUserId,
        DateTimeOffset startedAt,
        Guid? entityId = null,
        TelCallDirection direction = TelCallDirection.Inbound)
    {
        await using var db = _factory.CreateContext();

        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect(fromNumber);
        var to = protector.Protect("+22507000001");

        var call = TelCall.Open(
            _tenant, _connection, $"prov-{Guid.NewGuid()}",
            direction, TelCallStatus.Ringing,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            startedAt, externalUserId: null, crmUserId: crmUserId);

        call.TryMoveTo(status, startedAt.AddSeconds(20));

        if (entityId is { } id)
            call.Attach(TelMatchStatus.Matched, TelMatchMethod.Number, TelEntityTypes.Lead, id);

        db.TelCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        return call.Id;
    }

    private async Task<IReadOnlyList<MissedCallDto>> ReadAsync(DateTimeOffset? since = null)
    {
        await using var db = _factory.CreateContext();

        var result = await new ListMissedCallsHandler(
                db, TelephonyTestHarness.User(_tenant, _agent))
            .Handle(new ListMissedCallsQuery(since ?? Now.AddDays(-7)), CancellationToken.None);

        return result.Value;
    }
}
