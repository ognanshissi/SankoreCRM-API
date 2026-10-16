namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.MyStatus;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// "Does my telephony work?" (TEL-F-11) — the one telephony read an ordinary agent makes about
/// themselves.
///
/// <para>
/// Nothing here seeds a connection in the constructor, deliberately: the great majority of
/// tenants have no telephony at all and that branch is the one most likely to be reached in
/// production, so it has to be arranged explicitly rather than inherited from a fixture.
/// </para>
/// </summary>
public sealed class MyTelephonyStatusTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public MyTelephonyStatusTests() => _factory = new TestIntegrationDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    // ── No telephony at all ───────────────────────────────────────────────────

    [Fact]
    public async Task With_no_telephony_connection_the_answer_is_a_SUCCESSFUL_no()
    {
        var status = await ReadAsync(mayCall: true);

        status.TelephonyEnabled.Should().BeFalse();
        status.LinkStatus.Should().Be(GetMyTelephonyStatusHandler.NotLinked);
        status.CanMakeCalls.Should().BeFalse();
        status.ConnectionId.Should().BeNull();

        // A success and not a 404, because this is where most tenants live PERMANENTLY. A 404
        // would put an error in the browser console on every page load of every agent of every
        // institution that does not use telephony, and a front end would learn to ignore it.
        status.ExternalUserId.Should().BeNull();
        status.DefaultCallerNumber.Should().BeNull();
    }

    [Fact]
    public async Task An_INACTIVE_telephony_connection_counts_as_no_telephony()
    {
        await SeedConnectionAsync(active: false);
        await SeedLinkAsync(_me);

        var status = await ReadAsync(mayCall: true);

        // The ACTIVE connection is the switch and there is no second flag — activation requires a
        // passing health check, so an inactive row is a provider that has never answered. An
        // agent whose link is perfect still cannot place a call through a connection nobody
        // activated, and showing the button would make the refusal arrive at the handset.
        status.TelephonyEnabled.Should().BeFalse();
        status.CanMakeCalls.Should().BeFalse();
    }

    [Fact]
    public async Task An_active_connection_of_ANOTHER_family_does_not_switch_telephony_on()
    {
        await SeedConnectionAsync(active: true, family: IntegrationFamily.CoreBanking);

        // A tenant with a core-banking integration has an active connection and no telephone
        // system. Reading "any active connection" would render an Appeler button for all of them.
        (await ReadAsync(mayCall: true)).TelephonyEnabled.Should().BeFalse();
    }

    // ── Telephony on, this agent's own state ──────────────────────────────────

    [Fact]
    public async Task Telephony_on_and_nobody_paired_me_is_enabled_but_cannot_call()
    {
        await SeedConnectionAsync(active: true);

        var status = await ReadAsync(mayCall: true);

        // The institution has telephony, so the area belongs on the screen — but it has to say
        // WHY the button is missing, which is the whole reason this endpoint exists. "NotLinked"
        // names a repair an administrator can make; a hidden area names nothing.
        status.TelephonyEnabled.Should().BeTrue();
        status.LinkStatus.Should().Be(GetMyTelephonyStatusHandler.NotLinked);
        status.CanMakeCalls.Should().BeFalse();
        status.ConnectionId.Should().Be(_connection);
    }

    [Fact]
    public async Task An_ACTIVE_link_with_the_permission_can_call_and_reports_its_own_number()
    {
        await SeedConnectionAsync(active: true);
        await SeedLinkAsync(_me, externalUserId: "ringover-4412", callerNumber: "+22520000001");

        var status = await ReadAsync(mayCall: true);

        status.TelephonyEnabled.Should().BeTrue();
        status.LinkStatus.Should().Be(nameof(TelUserLinkStatus.Active));
        status.CanMakeCalls.Should().BeTrue();
        status.ExternalUserId.Should().Be("ringover-4412");

        // The one place this family returns a telephone number, and the difference is whose it
        // is: the institution's own outbound line, which the agent's own calls already present to
        // everybody they ring — never a customer's.
        status.DefaultCallerNumber.Should().Be("+22520000001");
    }

    [Fact]
    public async Task An_ACTIVE_link_without_the_permission_cannot_call()
    {
        await SeedConnectionAsync(active: true);
        await SeedLinkAsync(_me);

        var status = await ReadAsync(mayCall: false);

        // Pairing is not permission. A cashier may be listed at the provider because the
        // provider lists every extension, and the flag is read off the token's own claims so it
        // cannot disagree with what the Appeler endpoint would have refused.
        status.LinkStatus.Should().Be(nameof(TelUserLinkStatus.Active));
        status.CanMakeCalls.Should().BeFalse();
    }

    [Fact]
    public async Task A_BROKEN_link_says_so_and_still_allows_calls()
    {
        await SeedConnectionAsync(active: true);
        await SeedLinkAsync(_me, status: TelUserLinkStatus.Broken);

        var status = await ReadAsync(mayCall: true);

        status.LinkStatus.Should().Be(nameof(TelUserLinkStatus.Broken));

        // The judgement call of this slice, and the one a refactor is most likely to "tidy up"
        // into a boolean. Broken means the nightly check stopped finding the account at the
        // provider — a settings row to repair, and often a provider-side blip — not a decision
        // anybody made about this agent. Stopping their button would take an agent off the
        // telephone mid-shift over a sweep's verdict, while the indicator still tells them and
        // their administrator what to fix.
        status.CanMakeCalls.Should().BeTrue();
    }

    [Fact]
    public async Task A_DISABLED_link_stops_the_button()
    {
        await SeedConnectionAsync(active: true);
        await SeedLinkAsync(_me, status: TelUserLinkStatus.Disabled);

        var status = await ReadAsync(mayCall: true);

        status.LinkStatus.Should().Be(nameof(TelUserLinkStatus.Disabled));

        // The only status that does, and the reason is that somebody STATED one: an administrator
        // unpaired this agent with a reason on the record. Honouring a Broken link and refusing a
        // Disabled one is the whole difference between a fault and a decision.
        status.CanMakeCalls.Should().BeFalse();
    }

    // ── The subject is always the caller ──────────────────────────────────────

    [Fact]
    public async Task Another_agents_link_is_never_the_answer()
    {
        await SeedConnectionAsync(active: true);

        await SeedLinkAsync(
            _colleague, externalUserId: "ringover-9999", callerNumber: "+22520009999");

        var status = await ReadAsync(mayCall: true);

        // The read needs no permission beyond being signed in, and that is only defensible
        // because there is nothing of anybody else's to return. A colleague's provider account
        // and outbound line must not leak through the one telephony route that is open to every
        // user of the platform.
        status.LinkStatus.Should().Be(GetMyTelephonyStatusHandler.NotLinked);
        status.ExternalUserId.Should().BeNull();
        status.DefaultCallerNumber.Should().BeNull();
        status.CanMakeCalls.Should().BeFalse();
    }

    [Fact]
    public void The_query_carries_no_user_id_to_substitute()
    {
        // Structural, because it is what makes the permissionless endpoint safe: the subject is
        // ICurrentUser.Id and there is no parameter. A userId added later "for the admin screen"
        // would turn this into an unguarded read of anybody's pairing.
        typeof(GetMyTelephonyStatusQuery).GetProperties().Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_connection_and_link_are_not_mine()
    {
        var otherTenant = Guid.NewGuid();

        await SeedConnectionAsync(active: true, tenantId: otherTenant);
        await SeedLinkAsync(_me, tenantId: otherTenant);

        // No tenant predicate in the handler — the global query filter is the guarantee — and the
        // row is seeded for MY user id, so the filter is the only thing keeping it out.
        (await ReadAsync(mayCall: true)).TelephonyEnabled.Should().BeFalse();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task SeedConnectionAsync(
        bool active,
        IntegrationFamily family = IntegrationFamily.Telephony,
        Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenant;

        await using var db = _factory.ContextFor(tenant);

        if (active && family == IntegrationFamily.Telephony)
        {
            // Through the harness, which activates via the aggregate's own health-check rule
            // rather than around it.
            db.Connections.Add(
                TelephonyTestHarness.ActiveTelephonyConnection(tenant, _connection));
        }
        else
        {
            var connection = IntegrationConnection.Create(
                tenantId: tenant,
                family: family,
                kind: IntegrationKind.Fake,
                mode: IntegrationMode.Api,
                name: $"{family} test connection",
                settings: new FakeSettings { Family = family },
                createdBy: _me,
                clock: new TelephonyTestHarness.FixedClock(Now),
                id: _connection);

            if (active)
            {
                connection.RecordHealth(
                    IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), Now),
                    new TelephonyTestHarness.FixedClock(Now));

                connection.Activate(_me, new TelephonyTestHarness.FixedClock(Now))
                    .IsSuccess.Should().BeTrue();
            }

            db.Connections.Add(connection);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task SeedLinkAsync(
        Guid crmUserId,
        TelUserLinkStatus status = TelUserLinkStatus.Active,
        string externalUserId = "ringover-0001",
        string? callerNumber = null,
        Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenant;

        await using var db = _factory.ContextFor(tenant);

        var link = TelUserLink.Create(
            tenant, _connection, crmUserId, externalUserId, _me, Now,
            externalEmail: null, defaultCallerNumber: callerNumber);

        // Through the aggregate's own transitions, so a status that became unreachable would make
        // the case fail rather than silently assert against a hand-set field.
        switch (status)
        {
            case TelUserLinkStatus.Broken:
                link.MarkBroken("The provider stopped listing the account.");
                break;

            case TelUserLinkStatus.Disabled:
                link.Disable("Agent left the branch.").IsSuccess.Should().BeTrue();
                break;

            default:
                break;
        }

        db.TelUserLinks.Add(link);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<MyTelephonyStatusDto> ReadAsync(bool mayCall)
    {
        await using var db = _factory.CreateContext();

        var http = new DefaultHttpContext();

        var claims = new List<Claim>();

        if (mayCall)
            claims.Add(new Claim("permission", Permissions.CanMakeTelephonyCall.Code));

        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);

        var result = await new GetMyTelephonyStatusHandler(
                db, TelephonyTestHarness.User(_tenant, _me), accessor)
            .Handle(new GetMyTelephonyStatusQuery(), CancellationToken.None);

        // Never a refusal, whatever the state: there is always something true to display.
        result.IsSuccess.Should().BeTrue($"the read was refused with {result.Error}");

        return result.Value;
    }
}
