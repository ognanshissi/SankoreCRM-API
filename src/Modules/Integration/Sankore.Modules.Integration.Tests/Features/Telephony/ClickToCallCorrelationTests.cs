namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Xunit;

/// <summary>
/// Recognising an agent's own outbound call (TEL-B-06, §4.2).
///
/// <para>
/// A click-to-call is the ONLY certain attachment this module ever gets — every other one is a
/// number resolving to somebody who might be one of two people. So the cases here are about not
/// losing it, and about not claiming it when it is not there.
/// </para>
/// </summary>
public sealed class ClickToCallCorrelationTests : IDisposable
{
    private const string ClientNumber = "+22507222222";
    private const string AgentNumber = "+22507000001";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _recordId = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public ClickToCallCorrelationTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_provider_reference_correlates_exactly_and_attaches_the_record()
    {
        await SeedRequestAsync(externalRef: "prov-out-1");

        await RecordAsync(Outbound("prov-out-1"));

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.MatchStatus.Should().Be(TelMatchStatus.Matched);
        call.MatchMethod.Should().Be(TelMatchMethod.ClickToCall);
        call.EntityType.Should().Be(TelEntityTypes.Customer);
        call.EntityId.Should().Be(_recordId);
        call.ClickToCallId.Should().NotBeNull();

        // The request is settled: nothing more is written to it.
        db.TelClickToCalls.Single().Status.Should().Be(TelClickToCallStatus.Connected);
    }

    [Fact]
    public async Task Without_a_reference_the_user_number_and_window_correlate()
    {
        // The documented fallback. Right almost always, and wrong only for an agent who clicked
        // twice on two records inside two minutes — which is why the window is two minutes.
        await SeedRequestAsync(externalRef: null);

        await RecordAsync(Outbound("prov-out-1"));

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().MatchMethod.Should().Be(TelMatchMethod.ClickToCall);
    }

    [Fact]
    public async Task A_request_whose_window_has_closed_does_not_correlate()
    {
        await SeedRequestAsync(externalRef: null);

        // Five minutes later is a second attempt, and attaching it here would file the
        // conversation against a record the agent has left.
        await RecordAsync(Outbound("prov-out-1", startedAt: TelephonyTestHarness.Now.AddMinutes(5)));

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.ClickToCallId.Should().BeNull();
        call.MatchMethod.Should().NotBe(TelMatchMethod.ClickToCall);
    }

    [Fact]
    public async Task An_INBOUND_call_never_correlates_with_a_request_in_flight()
    {
        await SeedRequestAsync(externalRef: null);

        // Somebody ringing us while a click-to-call is in flight is a coincidence. Attaching it
        // would file an incoming conversation against whatever record the agent was looking at.
        await RecordAsync(Inbound("prov-in-1"));

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().ClickToCallId.Should().BeNull();
    }

    [Fact]
    public async Task Two_agents_waiting_on_the_same_number_correlate_with_neither()
    {
        // Two agents of one branch ringing the same shop within two minutes is ordinary, and
        // crediting one with the other's conversation is not a small error. Ambiguity is measured
        // in AGENTS: nothing correlates, the number match takes over, and the call is journalled
        // against the right record with nobody attributed — the honest of the two wrong answers.
        await SeedRequestAsync(externalRef: null);
        await SeedRequestAsync(externalRef: null, crmUserId: Guid.NewGuid());

        await RecordAsync(Outbound("prov-out-1"));

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.ClickToCallId.Should().BeNull();
        call.CrmUserId.Should().BeNull();
    }

    [Fact]
    public async Task One_agent_who_clicked_twice_correlates_with_their_second_click()
    {
        var first = await SeedRequestAsync(externalRef: null);
        var second = await SeedRequestAsync(
            externalRef: null, requestedAt: TelephonyTestHarness.Now.AddSeconds(20));

        await RecordAsync(Outbound("prov-out-1", startedAt: TelephonyTestHarness.Now.AddSeconds(25)));

        await using var db = _factory.CreateContext();

        // Not an ambiguity: they meant the second click, and two requests from ONE agent is a
        // double-click rather than two people.
        db.TelCalls.Single().ClickToCallId.Should().Be(second).And.NotBe(first);
    }

    [Fact]
    public async Task When_the_provider_names_the_account_another_agents_request_is_excluded()
    {
        // The provider said whose account placed the call, so the fallback narrows by user and
        // the colleague's request is out of the picture — no guessing required.
        await LinkAsync();
        await SeedRequestAsync(externalRef: null, crmUserId: Guid.NewGuid());

        await RecordAsync(Outbound("prov-out-1", providerUserId: FakeAdapter.SeededUser.Id.Value));

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.ClickToCallId.Should().BeNull();

        // Attributed all the same: the user link answered "whose handset", which is a different
        // question from "which record".
        call.CrmUserId.Should().Be(_agent);
    }

    [Fact]
    public async Task A_lone_request_correlates_even_when_the_provider_names_no_account()
    {
        // A callback API that returns neither a reference nor a user id is a real shape. Without
        // this the agent's own call would be journalled as an outbound to a number nobody
        // recognises, while the request that started it sat two rows away.
        await SeedRequestAsync(externalRef: null);

        await RecordAsync(Outbound("prov-out-1"));

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        call.MatchMethod.Should().Be(TelMatchMethod.ClickToCall);

        // And the correlated request answers "whose call" too, which the provider did not.
        call.CrmUserId.Should().Be(_agent);
    }

    [Fact]
    public async Task A_call_to_a_different_number_does_not_correlate()
    {
        await SeedRequestAsync(externalRef: null);

        await RecordAsync(Outbound("prov-out-1", toNumber: "+22507999999"));

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().ClickToCallId.Should().BeNull();
    }

    [Fact]
    public async Task A_click_to_call_attachment_survives_a_later_number_match()
    {
        await SeedRequestAsync(externalRef: "prov-out-1");

        await RecordAsync(Outbound("prov-out-1"));

        // The same call re-read by the catch-up sweep, with a client now resolving on the number.
        var someoneElse = Guid.NewGuid();

        await RecordAsync(
            Outbound("prov-out-1") with { Status = TelCallStatus.Completed },
            clientOnTheNumber: someoneElse);

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        // The agent said which record they were calling; a number resolving to somebody is a
        // weaker claim, and the journal must not replace the first with the second.
        call.EntityId.Should().Be(_recordId);
        call.EntityId.Should().NotBe(someoneElse);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task<Guid> SeedRequestAsync(
        string? externalRef, Guid? crmUserId = null, DateTimeOffset? requestedAt = null)
    {
        await using var db = _factory.CreateContext();

        var protectedNumber = TelephonyTestHarness.Numbers().Protect(ClientNumber);

        var request = TelClickToCall.Request(
            _tenant,
            _connection,
            crmUserId ?? _agent,
            TelEntityTypes.Customer,
            _recordId,
            nameof(TelephonyPhoneSlot.Primary),
            protectedNumber.Encrypted,
            protectedNumber.BlindIndex,
            requestedAt ?? TelephonyTestHarness.Now);

        // MarkAgentRinging with a null reference is the honest shape for a provider that returns
        // none: the status moves, the reference stays null, and the fallback is what correlates.
        request.MarkAgentRinging(externalRef);

        db.TelClickToCalls.Add(request);
        await db.SaveChangesAsync(CancellationToken.None);

        return request.Id;
    }

    private async Task LinkAsync()
    {
        await using var db = _factory.CreateContext();

        var result = await new Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser
                .LinkTelephonyUserHandler(
                db,
                TelephonyTestHarness.Access(db, new FakeAdapter()),
                TelephonyTestHarness.User(_tenant, _agent),
                TelephonyTestHarness.Clock,
                NullLogger<Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser
                    .LinkTelephonyUserHandler>.Instance)
            .Handle(
                new Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser
                    .LinkTelephonyUserCommand(_agent, FakeAdapter.SeededUser.Id.Value),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    private ProviderCall Outbound(
        string id,
        DateTimeOffset? startedAt = null,
        string toNumber = ClientNumber,
        string? providerUserId = null) => new(
        new ExternalId(id),
        TelCallDirection.Outbound,
        TelCallStatus.Ringing,
        FromNumber: AgentNumber,
        ToNumber: toNumber,
        StartedAt: startedAt ?? TelephonyTestHarness.Now.AddSeconds(3),
        AnsweredAt: null,
        EndedAt: null,
        RingSeconds: null,
        TalkSeconds: null,
        ProviderUserId: providerUserId is null ? null : new ExternalId(providerUserId),
        HasRecording: false);

    private ProviderCall Inbound(string id) => new(
        new ExternalId(id),
        TelCallDirection.Inbound,
        TelCallStatus.Ringing,
        FromNumber: ClientNumber,
        ToNumber: AgentNumber,
        StartedAt: TelephonyTestHarness.Now.AddSeconds(3),
        AnsweredAt: null,
        EndedAt: null,
        RingSeconds: null,
        TalkSeconds: null,
        ProviderUserId: null,
        HasRecording: false);

    private async Task RecordAsync(ProviderCall call, Guid? clientOnTheNumber = null)
    {
        await using var db = _factory.CreateContext();

        var customers = Substitute.For<ICustomersModule>();
        var leads = Substitute.For<ILeadsModule>();

        customers.FindMatchesAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(clientOnTheNumber is { } id
                ? [new CustomerMatch(
                    id, "CRM-1", "Quelqu'un d'autre", Guid.NewGuid(), null, "Active", "Approved",
                    ["phone"])]
                : []);

        leads.FindOpenLeadsByPhoneAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var writer = TelephonyTestHarness.Writer(db, customers, leads);

        await writer.RecordAsync(_tenant, _connection, call, CancellationToken.None);

        await db.SaveChangesAsync(CancellationToken.None);
    }
}
