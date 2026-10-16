namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Writing a provider's call into the journal, and attaching it to a record (TEL-B-08, TEL-B-09).
/// </summary>
public sealed class TelCallJournalTests : IDisposable
{
    private const string CustomerNumber = "+22507111111";
    private const string AgentNumber = "+22507000001";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    private ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private ILeadsModule _leads = Substitute.For<ILeadsModule>();

    public TelCallJournalTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();

        NoMatches();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_ring_opens_a_row_with_both_numbers_encrypted()
    {
        await RecordAsync(Ringing());

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.ExternalCallId.Should().Be("prov-1");
        call.Status.Should().Be(TelCallStatus.Ringing);

        // Encrypted and indexed — the number is personal data and the index is the only thing a
        // query may match on. A writer that forgot to encrypt would store the digits here.
        System.Text.Encoding.UTF8.GetString(call.FromNumberEnc)
            .Should().NotContain("07111111").And.StartWith("v1:");
        call.FromNumberBi.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_second_reading_of_the_same_provider_call_updates_one_row()
    {
        await RecordAsync(Ringing());
        await RecordAsync(Completed());

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.Status.Should().Be(TelCallStatus.Completed);
        call.TalkSeconds.Should().Be(90);
        call.AnsweredAt.Should().NotBeNull();
        call.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_transition_the_journal_forbids_leaves_the_last_known_status()
    {
        await RecordAsync(Completed());

        // Out-of-order delivery is ordinary: the ring arriving after the hang-up is a webhook
        // overtaking another, and handing an error back would have the provider redeliver it for
        // hours.
        await RecordAsync(Ringing());

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().Status.Should().Be(TelCallStatus.Completed);
    }

    [Fact]
    public async Task A_call_the_sweep_discovers_already_finished_carries_its_instants()
    {
        await RecordAsync(Completed());

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        // Opened straight on its final status, so the instants cannot come from a transition that
        // is never made — and a call answered but never ended makes every duration negative.
        call.AnsweredAt.Should().Be(Completed().AnsweredAt);
        call.EndedAt.Should().Be(Completed().EndedAt);
    }

    [Fact]
    public async Task A_provider_recording_marks_the_call_pending_and_not_available()
    {
        await RecordAsync(Completed());

        await using var db = _factory.CreateContext();

        // All this says is that the provider HAS one. Whether SANKORE keeps a copy is TEL-B-15's
        // policy, which ships Disabled.
        db.TelCalls.Single().RecordingStatus.Should().Be(TelRecordingStatus.Pending);
    }

    [Fact]
    public async Task The_agent_is_resolved_through_the_user_link()
    {
        await LinkAsync("fake-user-1");

        await RecordAsync(Completed());

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().CrmUserId.Should().Be(_agent);
    }

    [Fact]
    public async Task A_call_from_an_account_nobody_linked_is_journalled_with_no_agent()
    {
        await RecordAsync(Completed());

        await using var db = _factory.CreateContext();

        // Ordinary, not an error: the row is listed under "calls to attribute", and the nightly
        // link check is what explains why.
        db.TelCalls.Single().CrmUserId.Should().BeNull();
    }

    [Fact]
    public async Task A_broken_link_still_attributes_the_call()
    {
        await LinkAsync("fake-user-1");

        await using (var db = _factory.CreateContext())
        {
            var link = await db.TelUserLinks.AsTracking().SingleAsync(CancellationToken.None);
            link.MarkBroken("The account is no longer in the provider's directory.");
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await RecordAsync(Completed());

        await using var after = _factory.CreateContext();

        // The provider account stopped being listed; the agent is still the person who had the
        // conversation. Refusing to attribute until somebody repairs a settings row would lose
        // exactly the history the repair is meant to explain.
        after.TelCalls.Single().CrmUserId.Should().Be(_agent);
    }

    [Fact]
    public async Task A_disabled_link_stops_attributing()
    {
        await LinkAsync("fake-user-1");

        await using (var db = _factory.CreateContext())
        {
            var link = await db.TelUserLinks.AsTracking().SingleAsync(CancellationToken.None);
            link.Disable("L'agent a quitté l'institution.");
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await RecordAsync(Completed());

        await using var after = _factory.CreateContext();
        after.TelCalls.Single().CrmUserId.Should().BeNull();
    }

    // ── TEL-B-09 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_client_on_the_remote_number_attaches_the_call_to_them()
    {
        var clientId = Guid.NewGuid();
        _customers.FindMatchesAsync(_tenant, CustomerNumber, null, null, Arg.Any<CancellationToken>())
            .Returns([Client(clientId)]);

        await RecordAsync(Ringing());

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.MatchStatus.Should().Be(TelMatchStatus.Matched);
        call.EntityType.Should().Be(TelEntityTypes.Customer);
        call.EntityId.Should().Be(clientId);
        call.MatchMethod.Should().Be(TelMatchMethod.Number);
    }

    [Fact]
    public async Task A_client_wins_over_an_open_lead()
    {
        var clientId = Guid.NewGuid();
        _customers.FindMatchesAsync(_tenant, CustomerNumber, null, null, Arg.Any<CancellationToken>())
            .Returns([Client(clientId)]);
        _leads.FindOpenLeadsByPhoneAsync(_tenant, CustomerNumber, Arg.Any<CancellationToken>())
            .Returns([Lead(Guid.NewGuid())]);

        await RecordAsync(Ringing());

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        // The client record is where the conversation belongs, and it already lists the request.
        call.EntityType.Should().Be(TelEntityTypes.Customer);
        call.EntityId.Should().Be(clientId);
    }

    [Fact]
    public async Task An_open_lead_is_used_when_no_client_matches()
    {
        var leadId = Guid.NewGuid();
        _leads.FindOpenLeadsByPhoneAsync(_tenant, CustomerNumber, Arg.Any<CancellationToken>())
            .Returns([Lead(leadId)]);

        await RecordAsync(Ringing());

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.EntityType.Should().Be(TelEntityTypes.Lead);
        call.EntityId.Should().Be(leadId);
    }

    [Fact]
    public async Task Two_clients_on_one_number_are_left_for_a_human()
    {
        _customers.FindMatchesAsync(_tenant, CustomerNumber, null, null, Arg.Any<CancellationToken>())
            .Returns([Client(Guid.NewGuid()), Client(Guid.NewGuid())]);

        await RecordAsync(Ringing());

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        // A family handset or a shop line is ordinary, and guessing files a conversation against
        // the wrong person.
        call.MatchStatus.Should().Be(TelMatchStatus.Ambiguous);
        call.EntityId.Should().BeNull();
        call.EntityType.Should().BeNull();
    }

    [Fact]
    public async Task An_outbound_call_resolves_the_CALLEE_and_not_the_caller()
    {
        var clientId = Guid.NewGuid();
        _customers.FindMatchesAsync(_tenant, CustomerNumber, null, null, Arg.Any<CancellationToken>())
            .Returns([Client(clientId)]);

        // Getting this backwards would resolve the institution's own switchboard on every call.
        await RecordAsync(Completed() with
        {
            Direction = TelCallDirection.Outbound,
            FromNumber = AgentNumber,
            ToNumber = CustomerNumber,
        });

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().EntityId.Should().Be(clientId);
    }

    [Fact]
    public async Task An_unattached_call_is_re_matched_on_its_next_reading()
    {
        await RecordAsync(Ringing());

        await using (var db = _factory.CreateContext())
            db.TelCalls.Single().MatchStatus.Should().Be(TelMatchStatus.Unknown);

        // The lead this number belongs to was captured thirty seconds later, by the agent who
        // just answered.
        var leadId = Guid.NewGuid();
        _leads.FindOpenLeadsByPhoneAsync(_tenant, CustomerNumber, Arg.Any<CancellationToken>())
            .Returns([Lead(leadId)]);

        await RecordAsync(Completed());

        await using var after = _factory.CreateContext();
        after.TelCalls.Single().EntityId.Should().Be(leadId);
    }

    [Fact]
    public async Task A_manual_attachment_is_never_overwritten_by_a_later_sweep()
    {
        var chosen = Guid.NewGuid();

        await RecordAsync(Ringing());

        await using (var db = _factory.CreateContext())
        {
            var call = await db.TelCalls.AsTracking().SingleAsync(CancellationToken.None);
            call.Attach(TelMatchStatus.Matched, TelMatchMethod.Manual, TelEntityTypes.Lead, chosen)
                .IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync(CancellationToken.None);
        }

        _customers.FindMatchesAsync(_tenant, CustomerNumber, null, null, Arg.Any<CancellationToken>())
            .Returns([Client(Guid.NewGuid())]);

        await RecordAsync(Completed());

        await using var after = _factory.CreateContext();
        var row = after.TelCalls.Single();

        // A human looked at this call. A later sweep must not undo their reading.
        row.EntityId.Should().Be(chosen);
        row.MatchMethod.Should().Be(TelMatchMethod.Manual);
    }

    [Fact]
    public async Task An_outage_in_another_module_journals_the_call_unattached()
    {
        _customers.FindMatchesAsync(
                _tenant, CustomerNumber, null, null, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CustomerMatch>>(_ => throw new InvalidOperationException("M01 down"));

        await RecordAsync(Ringing());

        await using var db = _factory.CreateContext();

        // Losing the journal because M01 was down would lose the one record of a conversation
        // that actually happened.
        db.TelCalls.Should().HaveCount(1);
        db.TelCalls.Single().MatchStatus.Should().Be(TelMatchStatus.Unknown);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private void NoMatches()
    {
        _customers = Substitute.For<ICustomersModule>();
        _leads = Substitute.For<ILeadsModule>();

        _customers.FindMatchesAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        _leads.FindOpenLeadsByPhoneAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private static CustomerMatch Client(Guid id) => new(
        id, "CRM-000123", "Aya Kouassi", Guid.NewGuid(), null, "Active", "Approved", ["phone"]);

    private static LeadPhoneMatch Lead(Guid id) => new(id, "Aya Kouassi", "New", null, null);

    private static ProviderCall Ringing() => new(
        new ExternalId("prov-1"),
        TelCallDirection.Inbound,
        TelCallStatus.Ringing,
        FromNumber: CustomerNumber,
        ToNumber: AgentNumber,
        StartedAt: TelephonyTestHarness.Now,
        AnsweredAt: null,
        EndedAt: null,
        RingSeconds: null,
        TalkSeconds: null,
        ProviderUserId: new ExternalId("fake-user-1"),
        HasRecording: false);

    private static ProviderCall Completed() => new(
        new ExternalId("prov-1"),
        TelCallDirection.Inbound,
        TelCallStatus.Completed,
        FromNumber: CustomerNumber,
        ToNumber: AgentNumber,
        StartedAt: TelephonyTestHarness.Now,
        AnsweredAt: TelephonyTestHarness.Now.AddSeconds(6),
        EndedAt: TelephonyTestHarness.Now.AddSeconds(96),
        RingSeconds: 6,
        TalkSeconds: 90,
        ProviderUserId: new ExternalId("fake-user-1"),
        HasRecording: true);

    private async Task RecordAsync(ProviderCall call)
    {
        await using var db = _factory.CreateContext();

        var writer = TelephonyTestHarness.Writer(db, _customers, _leads);

        await writer.RecordAsync(_tenant, _connection, call, CancellationToken.None);

        // The writer deliberately does NOT save: every caller is inside a transaction that owns
        // more than this row.
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task LinkAsync(string externalUserId)
    {
        await using var db = _factory.CreateContext();

        var result = await new LinkTelephonyUserHandler(
                db,
                TelephonyTestHarness.Access(db, new FakeAdapter()),
                TelephonyTestHarness.User(_tenant, _agent),
                TelephonyTestHarness.Clock,
                NullLogger<LinkTelephonyUserHandler>.Instance)
            .Handle(new LinkTelephonyUserCommand(_agent, externalUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
