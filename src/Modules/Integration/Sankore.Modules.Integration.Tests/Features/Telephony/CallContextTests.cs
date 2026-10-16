namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony;
using Sankore.Modules.Integration.Features.Telephony.CallContext;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Features.Telephony.LinkCall;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// The screen pop's two halves (TEL-B-10): a push that carries a call id and nothing else, and a
/// read that turns it into a name under the caller's permission and perimeter.
/// </summary>
public sealed class CallContextTests : IDisposable
{
    private const string CustomerNumber = "+22507111111";
    private const string AgentNumber = "+22507000001";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _myAgency = Guid.NewGuid();
    private readonly Guid _otherAgency = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    private ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private ILeadsModule _leads = Substitute.For<ILeadsModule>();

    public CallContextTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();

        NoCandidates();
    }

    public void Dispose() => _factory.Dispose();

    // ── The push ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_ring_pushes_the_call_id_and_nothing_else()
    {
        var (pop, notifier) = TelephonyTestHarness.ScreenPop();

        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        await pop.PushRingingAsync(call, CancellationToken.None);

        var push = notifier.Pushes.Should().ContainSingle().Subject;

        push.TenantId.Should().Be(_tenant);
        push.UserId.Should().Be(_agent);
        push.EventName.Should().Be(TelephonyScreenPop.RingingEvent);

        // The payload's whole shape. A websocket frame is delivered to whoever holds the
        // connection, with no permission check and no perimeter per frame — so a name here would
        // be personal data leaving the building on a connection nobody re-authorises.
        push.Payload.Should().BeOfType<TelephonyRingingPush>()
            .Which.CallId.Should().Be(call.Id);

        push.Payload.GetType().GetProperties().Should().HaveCount(1);
    }

    [Theory]
    [InlineData(TelCallStatus.InProgress)]
    [InlineData(TelCallStatus.Completed)]
    [InlineData(TelCallStatus.Missed)]
    public async Task Only_a_RING_is_pushed(TelCallStatus status)
    {
        var (pop, notifier) = TelephonyTestHarness.ScreenPop();

        var call = await SeedCallAsync(status, crmUserId: _agent);

        // The agent is holding the handset by then and knows perfectly well what happened.
        await pop.PushRingingAsync(call, CancellationToken.None);

        notifier.Pushes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_call_nobody_is_linked_to_pushes_to_nobody()
    {
        var (pop, notifier) = TelephonyTestHarness.ScreenPop();

        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: null);

        // There is no agent to tell. The call is journalled and appears in "calls to attribute",
        // which is the honest outcome.
        await pop.PushRingingAsync(call, CancellationToken.None);

        notifier.Pushes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_undelivered_push_is_not_an_error()
    {
        var (pop, notifier) = TelephonyTestHarness.ScreenPop();
        notifier.Delivers = false;

        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        // An agent with no open tab is the ordinary case for most of the day.
        var act = async () => await pop.PushRingingAsync(call, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── The read ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_that_does_not_exist_is_not_found()
    {
        var result = await ReadAsync(Guid.NewGuid());

        result.Error.Should().Be(TelephonyErrors.CallNotFound);
    }

    [Fact]
    public async Task Another_tenants_call_is_not_found_rather_than_forbidden()
    {
        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        // The existence of a call is itself information about who the institution spoke to.
        var result = await ReadAsync(call.Id, tenantId: Guid.NewGuid());

        result.Error.Should().Be(TelephonyErrors.CallNotFound);
    }

    [Fact]
    public async Task A_number_nobody_holds_answers_no_candidate_and_not_a_failure()
    {
        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        var result = await ReadAsync(call.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.CallId.Should().Be(call.Id);
        result.Value.MatchStatus.Should().Be(nameof(TelMatchStatus.Unknown));
        result.Value.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task A_candidate_inside_the_perimeter_is_named()
    {
        var clientId = Guid.NewGuid();
        CandidateIs(clientId, _myAgency);

        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        var result = await ReadAsync(call.Id, scope: Perimeter(_myAgency));

        var candidate = result.Value.Candidates.Should().ContainSingle().Subject;

        candidate.EntityType.Should().Be(TelEntityTypes.Customer);
        candidate.EntityId.Should().Be(clientId);
        candidate.DisplayName.Should().Be("Aya Kouassi");
        candidate.OutOfPerimeter.Should().BeFalse();
    }

    [Fact]
    public async Task A_candidate_outside_the_perimeter_is_listed_WITHOUT_a_name()
    {
        var clientId = Guid.NewGuid();
        CandidateIs(clientId, _otherAgency);

        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        var result = await ReadAsync(call.Id, scope: Perimeter(_myAgency));

        var candidate = result.Value.Candidates.Should().ContainSingle().Subject;

        // The agent learns the caller is known to the institution elsewhere, which stops them
        // searching for somebody they will never find — and the record itself stays hidden.
        candidate.OutOfPerimeter.Should().BeTrue();
        candidate.DisplayName.Should().BeNull();
        candidate.EntityId.Should().Be(clientId);
    }

    [Fact]
    public async Task An_unrestricted_caller_sees_every_name()
    {
        CandidateIs(Guid.NewGuid(), _otherAgency);

        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        // A null scope means "apply no filter" and never "no access" — IAgencyScopeProvider is
        // explicit about it, and reading it the other way would lock a super-user out of every
        // screen pop.
        var result = await ReadAsync(call.Id, scope: null);

        result.Value.Candidates.Single().OutOfPerimeter.Should().BeFalse();
    }

    [Fact]
    public async Task A_lead_with_no_agency_is_named_only_to_an_unrestricted_caller()
    {
        var leadId = Guid.NewGuid();
        LeadCandidateIs(leadId, agencyId: null);

        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        // A lead nobody has assigned sits in NO perimeter. Reading null as "inside all of them"
        // would show an unassigned prospect's name to every agent of the tenant.
        (await ReadAsync(call.Id, scope: Perimeter(_myAgency)))
            .Value.Candidates.Single().OutOfPerimeter.Should().BeTrue();

        (await ReadAsync(call.Id, scope: null))
            .Value.Candidates.Single().OutOfPerimeter.Should().BeFalse();
    }

    [Fact]
    public async Task An_ATTACHED_call_answers_its_attachment_and_does_not_re_resolve()
    {
        var chosen = Guid.NewGuid();

        var call = await SeedCallAsync(TelCallStatus.InProgress, crmUserId: _agent);

        await LinkAsync(call.Id, chosen);

        // The number now resolves to somebody else entirely — a shop line reassigned, or simply
        // the second person on a family handset.
        CandidateIs(Guid.NewGuid(), _myAgency);

        var result = await ReadAsync(call.Id, scope: Perimeter(_myAgency));

        var candidate = result.Value.Candidates.Should().ContainSingle().Subject;

        // A human looked at this call. Re-resolving would quietly replace their decision with a
        // lookup.
        candidate.EntityId.Should().Be(chosen);
        result.Value.MatchStatus.Should().Be(nameof(TelMatchStatus.Matched));
    }

    [Fact]
    public async Task Two_candidates_are_both_listed_so_the_agent_can_choose()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        _customers.FindMatchesAsync(
                _tenant, CustomerNumber, null, null, Arg.Any<CancellationToken>())
            .Returns([Client(first, _myAgency), Client(second, _myAgency)]);

        var call = await SeedCallAsync(TelCallStatus.Ringing, crmUserId: _agent);

        var result = await ReadAsync(call.Id, scope: Perimeter(_myAgency));

        // Ambiguous is only useful to an agent who can see the two names it is between.
        result.Value.Candidates.Should().HaveCount(2);
        result.Value.Candidates.Select(c => c.EntityId).Should().Contain([first, second]);
    }

    [Fact]
    public async Task An_anonymised_call_answers_no_candidate()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed, crmUserId: _agent, anonymised: true);

        CandidateIs(Guid.NewGuid(), _myAgency);

        var result = await ReadAsync(call.Id, scope: Perimeter(_myAgency));

        // The numbers are blanked and the row kept, so there is nothing to resolve — which is
        // what the screen shows for a withheld caller id anyway.
        result.IsSuccess.Should().BeTrue();
        result.Value.Candidates.Should().BeEmpty();
    }

    // ── The manual attachment's two gates ─────────────────────────────────────

    [Fact]
    public async Task A_call_attributed_to_another_agent_cannot_be_re_attached()
    {
        var colleague = Guid.NewGuid();
        var call = await SeedCallAsync(TelCallStatus.Completed, crmUserId: colleague);

        var result = await TryLinkAsync(call.Id, Guid.NewGuid(), recordAllowed: true);

        // Not found and not forbidden: a 403 would confirm that a colleague spoke to somebody.
        result.Error.Should().Be(TelephonyErrors.CallNotFound);

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().EntityId.Should().BeNull("nothing was attached");
    }

    [Fact]
    public async Task A_call_nobody_is_attributed_may_be_attached_by_anyone_who_may_link()
    {
        // The agency's "calls to attribute" queue, which is the case that needs attaching most.
        var call = await SeedCallAsync(TelCallStatus.Completed, crmUserId: null);

        var chosen = Guid.NewGuid();

        (await TryLinkAsync(call.Id, chosen, recordAllowed: true)).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().EntityId.Should().Be(chosen);
    }

    [Fact]
    public async Task A_record_the_caller_may_not_work_is_refused_and_nothing_is_written()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed, crmUserId: _agent);

        // An arbitrary Guid in a request body. Without the target check the call would land in
        // that record's history — which GetCallsForEntityAsync projects with no perimeter of its
        // own, so another branch's client file would carry this conversation.
        var result = await TryLinkAsync(call.Id, Guid.NewGuid(), recordAllowed: false);

        result.Error.Should().Be(TelephonyErrors.EntityNotYours);

        await using var db = _factory.CreateContext();
        var row = db.TelCalls.Single();

        row.EntityId.Should().BeNull();
        row.MatchMethod.Should().BeNull();
    }

    [Fact]
    public async Task An_outage_in_the_owning_module_refuses_the_attachment()
    {
        var call = await SeedCallAsync(TelCallStatus.Completed, crmUserId: _agent);

        var customers = Substitute.For<ICustomersModule>();
        customers.CanActorAccessClientAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("M01 down"));

        await using var db = _factory.CreateContext();

        var result = await new LinkCallHandler(
                db,
                new TelephonyRecordAccess(
                    customers, _leads, NullLogger<TelephonyRecordAccess>.Instance),
                TelephonyTestHarness.User(_tenant, _agent),
                NullLogger<LinkCallHandler>.Instance)
            .Handle(
                new LinkCallCommand(
                    call.Id, TelEntityTypes.Customer, Guid.NewGuid(), "L'agent a corrigé."),
                CancellationToken.None);

        // Fails CLOSED — the authorization posture, not the capture path's. A check that did not
        // happen must not read as a pass.
        result.Error.Should().Be(TelephonyErrors.EntityNotYours);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private void NoCandidates()
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

    private void CandidateIs(Guid clientId, Guid agencyId)
        => _customers.FindMatchesAsync(
                _tenant, CustomerNumber, null, null, Arg.Any<CancellationToken>())
            .Returns([Client(clientId, agencyId)]);

    private void LeadCandidateIs(Guid leadId, Guid? agencyId)
        => _leads.FindOpenLeadsByPhoneAsync(
                _tenant, CustomerNumber, Arg.Any<CancellationToken>())
            .Returns([new LeadPhoneMatch(leadId, "Koffi Kouassi", "New", agencyId, null)]);

    /// <summary>
    /// A perimeter as <c>IAgencyScopeProvider</c> hands one back. A helper because a collection
    /// expression cannot build an <c>IReadOnlySet</c>, and spelling the HashSet out at seven call
    /// sites would bury what each case is actually saying.
    /// </summary>
    private static IReadOnlySet<Guid> Perimeter(params Guid[] agencies) => agencies.ToHashSet();

    private static CustomerMatch Client(Guid id, Guid agencyId) => new(
        id, "CRM-000123", "Aya Kouassi", agencyId, null, "Active", "Approved", ["phone"]);

    private async Task<TelCall> SeedCallAsync(
        TelCallStatus status, Guid? crmUserId, bool anonymised = false)
    {
        await using var db = _factory.CreateContext();

        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect(CustomerNumber);
        var to = protector.Protect(AgentNumber);

        var call = TelCall.Open(
            _tenant, _connection, $"prov-{Guid.NewGuid()}",
            TelCallDirection.Inbound, status,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            TelephonyTestHarness.Now, externalUserId: null, crmUserId: crmUserId);

        if (anonymised) call.Anonymise();

        db.TelCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        return call;
    }

    private async Task LinkAsync(Guid callId, Guid entityId)
    {
        (await TryLinkAsync(callId, entityId, recordAllowed: true)).IsSuccess.Should().BeTrue();
    }

    private async Task<Sankore.Shared.Kernel.Result> TryLinkAsync(
        Guid callId, Guid entityId, bool recordAllowed)
    {
        await using var db = _factory.CreateContext();

        return await new LinkCallHandler(
                db,
                TelephonyTestHarness.RecordAccess(recordAllowed),
                TelephonyTestHarness.User(_tenant, _agent),
                NullLogger<LinkCallHandler>.Instance)
            .Handle(
                new LinkCallCommand(callId, TelEntityTypes.Customer, entityId, "L'agent a corrigé."),
                CancellationToken.None);
    }

    private async Task<Sankore.Shared.Kernel.Result<TelephonyCallContext>> ReadAsync(
        Guid callId, Guid? tenantId = null, IReadOnlySet<Guid>? scope = null)
    {
        await using var db = _factory.ContextFor(tenantId ?? _tenant);

        var agencyScope = Substitute.For<IAgencyScopeProvider>();
        agencyScope.GetAccessibleAgencyIdsAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(scope);

        return await new GetCallContextHandler(
                db,
                new TelephonyCallMatcher(
                    _customers, _leads, NullLogger<TelephonyCallMatcher>.Instance),
                TelephonyTestHarness.Numbers(),
                agencyScope,
                TelephonyTestHarness.User(tenantId ?? _tenant, _agent),
                NullLogger<GetCallContextHandler>.Instance)
            .Handle(new GetCallContextQuery(callId), CancellationToken.None);
    }
}
