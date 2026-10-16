namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The agency on a call (TEL-B-19): where it comes from, when it is filled in, and — the property
/// everything downstream rests on — that it is <b>never moved afterwards</b>.
/// </summary>
public sealed class TelephonyAgencyStampTests : IDisposable
{
    private const string CustomerNumber = "+22507111111";
    private const string AgentNumber = "+22507000001";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();

    public TelephonyAgencyStampTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();

        _customers.FindMatchesAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        _leads.FindOpenLeadsByPhoneAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_journalled_call_carries_the_agency_its_agent_was_in()
    {
        await LinkAsync();

        await RecordAsync(Ringing(), agencyOfAgent: _branch);

        (await ReadAsync()).AgencyId.Should().Be(_branch);
    }

    [Fact]
    public async Task A_call_nobody_is_attributed_carries_no_agency_and_M12_is_never_asked()
    {
        // No link, so no agent — and an unattributed call has no agency by construction rather
        // than by failure. The resolver returns before troubling M12.
        await RecordAsync(Ringing(), agencyOfAgent: _branch);

        var call = await ReadAsync();

        call.CrmUserId.Should().BeNull();
        call.AgencyId.Should().BeNull();
    }

    [Fact]
    public async Task An_agent_with_no_agency_leaves_the_call_with_none()
    {
        await LinkAsync();

        // M12 permits a user with no agency and documents it as ordinary for a head-office
        // account. Null means "outside every restricted perimeter", never "inside all of them".
        await RecordAsync(Ringing(), agencyOfAgent: null);

        (await ReadAsync()).AgencyId.Should().BeNull();
    }

    [Fact]
    public async Task M12_unreachable_at_the_ring_is_repaired_by_the_NEXT_reading()
    {
        await LinkAsync();

        // The blip: the call is journalled with an agent and no agency.
        await RecordAsync(Ringing(), agencyOfAgent: null);

        (await ReadAsync()).AgencyId.Should().BeNull();

        // A call produces two to four readings, so the next webhook fills it in. Without this the
        // call would sit outside every agency for ever over a few seconds of downtime.
        await RecordAsync(Completed(), agencyOfAgent: _branch);

        (await ReadAsync()).AgencyId.Should().Be(_branch);
    }

    [Fact]
    public async Task An_agent_who_changes_branch_does_NOT_take_their_old_calls_with_them()
    {
        await LinkAsync();

        await RecordAsync(Ringing(), agencyOfAgent: _branch);

        var newBranch = Guid.NewGuid();

        // The same call read again after the transfer. This is the single most important property
        // of the column: a figure a manager read last month must not change, and the manager who
        // was answerable for a conversation must keep being the one who may review it.
        await RecordAsync(Completed(), agencyOfAgent: newBranch);

        (await ReadAsync()).AgencyId.Should().Be(_branch);
    }

    [Fact]
    public void StampAgency_refuses_Guid_Empty_as_an_agency()
    {
        var call = NewCall();

        // Guid.Empty is how M12 flattens "no agency" on an AgentSummary, and it is not an agency.
        call.StampAgency(Guid.Empty).Should().BeFalse();
        call.AgencyId.Should().BeNull();

        call.StampAgency(_branch).Should().BeTrue();
        call.AgencyId.Should().Be(_branch);

        // Write-once, asserted on the aggregate so the rule holds for any future caller.
        call.StampAgency(Guid.NewGuid()).Should().BeFalse();
        call.AgencyId.Should().Be(_branch);
    }

    [Fact]
    public void Anonymising_a_call_keeps_its_agency()
    {
        var call = NewCall();
        call.StampAgency(_branch);

        call.Anonymise();

        // The agency is a property of the institution, not of the person — and destroying it
        // would turn a retention rule into a reporting outage.
        call.AgencyId.Should().Be(_branch);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private TelCall NewCall()
    {
        var number = TelephonyTestHarness.Numbers().Protect(CustomerNumber);

        return TelCall.Open(
            _tenant, _connection, "prov-1",
            TelCallDirection.Inbound, TelCallStatus.Ringing,
            number.Encrypted, number.BlindIndex, number.Encrypted, number.BlindIndex,
            TelephonyTestHarness.Now, crmUserId: _agent);
    }

    private ProviderCall Ringing() => new(
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

    private ProviderCall Completed() => new(
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
        HasRecording: false);

    private async Task RecordAsync(ProviderCall call, Guid? agencyOfAgent)
    {
        await using var db = _factory.CreateContext();

        await TelephonyTestHarness.Writer(db, _customers, _leads, agencyOfAgent)
            .RecordAsync(_tenant, _connection, call, CancellationToken.None);

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<TelCall> ReadAsync()
    {
        await using var db = _factory.CreateContext();

        return await db.TelCalls.FirstAsync(CancellationToken.None);
    }

    private async Task LinkAsync()
    {
        await using var db = _factory.CreateContext();

        var result = await new LinkTelephonyUserHandler(
                db,
                TelephonyTestHarness.Access(db, new FakeAdapter()),
                TelephonyTestHarness.User(_tenant, _agent),
                TelephonyTestHarness.Clock,
                NullLogger<LinkTelephonyUserHandler>.Instance)
            .Handle(new LinkTelephonyUserCommand(_agent, "fake-user-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
