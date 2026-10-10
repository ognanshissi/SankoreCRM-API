namespace Sankore.Modules.Leads.Tests.Features.Household;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Household.GetHousehold;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers the household READ (BNF-B-03, BNF-B-05) — its perimeter, its PEP gate and its address
/// resolution.
/// </summary>
public sealed class GetHouseholdHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _holder = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public GetHouseholdHandlerTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private GetHouseholdHandler Handler(Guid callerId, params string[] roles)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(callerId);
        user.TenantId.Returns(_tenant);
        user.Roles.Returns(roles.Length == 0 ? [Roles.CommercialAgent.Code] : roles);

        return new GetHouseholdHandler(
            _factory.CreateContext(),
            _admin,
            new LeadAccessGuard(user, _admin),
            new FrozenClock(Now));
    }

    private async Task<Lead> SeedAsync(bool withSpouse = true, bool exposed = false)
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System,
            firstName: "Aya", lastName: "Kouassi", ownerId: _holder);

        if (withSpouse)
        {
            lead.AddHouseholdMember(
                HouseholdMemberType.Spouse, SpouseUnionType.Married,
                PersonIdentity.Create("Kouassi", "Awa", birthDate: new DateOnly(1990, 4, 2)),
                null, usesPrincipalAddress: true, now: Now);
        }

        if (exposed)
        {
            lead.SetPrincipalPepStatus(
                PepStatus.Exposed("Ministre des Finances", PepLink.Self, "CI"), Now, 12);
        }

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);
        return lead;
    }

    // ── The perimeter (the security review's finding) ─────────────────────

    [Fact]
    public async Task The_holder_can_read_the_household()
    {
        var lead = await SeedAsync();

        var result = await Handler(_holder).Handle(
            new GetHouseholdQuery(lead.Id, IncludePepDetail: false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Members.Should().ContainSingle();
    }

    [Fact]
    public async Task Another_agent_in_the_same_tenant_CANNOT_read_the_household()
    {
        // The point of the fix: a colleague could previously SEE a spouse and children they had no
        // business seeing, while being refused the write. The tenant filter and `lead:read` alone
        // are the right posture for a phone number, not for this.
        var lead = await SeedAsync();
        var stranger = Guid.NewGuid();
        _admin.GetTeamAgentIdsAsync(_tenant, stranger, Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var result = await Handler(stranger).Handle(
            new GetHouseholdQuery(lead.Id, IncludePepDetail: false), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(LeadAccessGuard.Forbidden);
    }

    [Fact]
    public async Task The_holders_supervisor_can_read_the_household()
    {
        var lead = await SeedAsync();
        var supervisor = Guid.NewGuid();
        _admin.GetTeamAgentIdsAsync(_tenant, supervisor, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _holder });

        (await Handler(supervisor).Handle(
                new GetHouseholdQuery(lead.Id, IncludePepDetail: false), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_M12_outage_does_not_lock_the_holder_out_of_their_own_lead()
    {
        // Why EnsureCanWorkAsync is safe on a read path: direct ownership is checked BEFORE the
        // cross-module call, so the holder never reaches it.
        var lead = await SeedAsync();
        _admin.GetTeamAgentIdsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Guid>>>(_ => throw new HttpRequestException("M12 down"));

        (await Handler(_holder).Handle(
                new GetHouseholdQuery(lead.Id, IncludePepDetail: false), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_M12_outage_DOES_refuse_an_unverifiable_supervisor()
    {
        var lead = await SeedAsync();
        _admin.GetTeamAgentIdsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Guid>>>(_ => throw new HttpRequestException("M12 down"));

        (await Handler(Guid.NewGuid()).Handle(
                new GetHouseholdQuery(lead.Id, IncludePepDetail: false), CancellationToken.None))
            .IsFailure.Should().BeTrue("supervision is exactly what cannot be verified during an outage");
    }

    [Fact]
    public async Task A_missing_lead_answers_not_found_before_any_perimeter_check()
    {
        var result = await Handler(_holder).Handle(
            new GetHouseholdQuery(Guid.NewGuid(), IncludePepDetail: false), CancellationToken.None);

        result.Error.Should().Be("LEAD_NOT_FOUND");
    }

    // ── The PEP gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task Without_the_pep_permission_the_flag_shows_but_the_detail_does_not()
    {
        // Dispatching has to route an exposed lead to a cleared agent without showing anybody why.
        var lead = await SeedAsync(exposed: true);

        var result = await Handler(_holder).Handle(
            new GetHouseholdQuery(lead.Id, IncludePepDetail: false), CancellationToken.None);

        result.Value.HasPepExposure.Should().BeTrue();
        result.Value.PrincipalIsPepExposed.Should().BeTrue();
        result.Value.PrincipalPep.Should().BeNull("the function and country are the gated part");
        result.Value.Members[0].IsPepExposed.Should().BeTrue();
        result.Value.Members[0].Pep.Should().BeNull();
    }

    [Fact]
    public async Task With_the_pep_permission_the_detail_is_returned()
    {
        var lead = await SeedAsync(exposed: true);

        var result = await Handler(_holder).Handle(
            new GetHouseholdQuery(lead.Id, IncludePepDetail: true), CancellationToken.None);

        result.Value.PrincipalPep!.Function.Should().Be("Ministre des Finances");
        result.Value.PrincipalPep.CountryCode.Should().Be("CI");
        result.Value.Members[0].Pep!.Link.Should().Be(nameof(PepLink.FamilyMember));
    }

    // ── What the read resolves ────────────────────────────────────────────

    [Fact]
    public async Task An_inheriting_member_is_returned_with_the_principals_address()
    {
        // Reading Identity directly would show a spouse with no address, which looks like missing
        // data rather than inheritance — and would be reported as incomplete.
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System,
            firstName: "Aya", lastName: "Kouassi", ownerId: _holder);

        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi", birthDate: new DateOnly(2015, 6, 1)),
            null, usesPrincipalAddress: true, now: Now);

        await using (var db = _factory.CreateContext())
        {
            db.Leads.Add(lead);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var result = await Handler(_holder).Handle(
            new GetHouseholdQuery(lead.Id, IncludePepDetail: false), CancellationToken.None);

        var child = result.Value.Members.Single();
        child.UsesPrincipalAddress.Should().BeTrue();
        // The principal of a lead carries no address of its own yet (BNF-B-01 keeps those fields
        // optional), so there is nothing to inherit — and the flag still says where it comes from.
        child.Age.Should().Be(11, "the age is computed at the time of the read");
    }

    [Fact]
    public async Task A_removed_member_is_returned_flagged_rather_than_dropped()
    {
        var lead = await SeedAsync();

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads
                .AsTracking()
                .Include(l => l.HouseholdMembers)
                .SingleAsync(CancellationToken.None);

            tracked.RemoveHouseholdMember(
                tracked.HouseholdMembers[0].Id, "séparation", Now);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var result = await Handler(_holder).Handle(
            new GetHouseholdQuery(lead.Id, IncludePepDetail: false), CancellationToken.None);

        var member = result.Value.Members.Should().ContainSingle().Subject;
        member.IsActive.Should().BeFalse();
        member.RemovalReason.Should().Be("séparation");
    }
}
