namespace Sankore.Modules.Leads.Tests.Features.Household;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers <see cref="LeadAccessGuard"/> — "l'agent assigné ou un superviseur" (BNF-B-03).
/// </summary>
public sealed class LeadAccessGuardTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _holder = Guid.NewGuid();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    private LeadAccessGuard Guard(Guid? callerId = null, params string[] roles)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(callerId ?? _me);
        user.TenantId.Returns(Tenant);
        user.Roles.Returns(roles.Length == 0 ? [Roles.CommercialAgent.Code] : roles);

        return new LeadAccessGuard(user, _admin);
    }

    private static Lead MakeLead(Guid? ownerId = null, Guid? assignedId = null)
    {
        var lead = Lead.Capture(
            tenantId: Tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Crédit", preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System, ownerId: ownerId);

        if (assignedId is { } agent)
        {
            var assignment = LeadAssignment.Create(
                tenantId: Tenant, leadId: lead.Id, agentId: agent,
                strategy: DispatchingStrategy.RoundRobin, compatibilityScore: 50,
                slaDeadline: DateTimeOffset.UtcNow.AddHours(4), createdAt: DateTimeOffset.UtcNow);
            lead.AssignTo(assignment, null);
        }

        return lead;
    }

    private void TeamOf(Guid supervisor, params Guid[] members) =>
        _admin.GetTeamAgentIdsAsync(Tenant, supervisor, Arg.Any<CancellationToken>())
            .Returns(members.ToList());

    [Fact]
    public async Task The_assigned_agent_may_work_the_lead()
    {
        (await Guard().EnsureCanWorkAsync(MakeLead(assignedId: _me), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_owner_may_work_the_lead()
    {
        (await Guard().EnsureCanWorkAsync(MakeLead(ownerId: _me), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Another_agent_may_not()
    {
        TeamOf(_me);   // an empty team

        var result = await Guard().EnsureCanWorkAsync(
            MakeLead(assignedId: _holder), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(LeadAccessGuard.Forbidden);
    }

    [Fact]
    public async Task A_supervisor_of_the_holder_may()
    {
        TeamOf(_me, _holder);

        (await Guard().EnsureCanWorkAsync(MakeLead(assignedId: _holder), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Administrator")]
    [InlineData("SalesManager")]
    public async Task The_bypass_roles_reach_any_lead_without_a_team_lookup(string role)
    {
        // A manager whose team comes back empty — a one-agency tenant — would otherwise be locked
        // out of every lead by a contract answering "nobody reports to you".
        var guard = Guard(callerId: Guid.NewGuid(), roles: role);

        (await guard.EnsureCanWorkAsync(MakeLead(assignedId: _holder), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        await _admin.DidNotReceive().GetTeamAgentIdsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_branch_manager_does_NOT_bypass()
    {
        // Their reach is their branch, which is exactly what GetTeamAgentIdsAsync computes —
        // bypassing would replace a real perimeter with a blanket one.
        TeamOf(_me);

        (await Guard(roles: Roles.BranchManager.Code)
                .EnsureCanWorkAsync(MakeLead(assignedId: _holder), CancellationToken.None))
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_lead_nobody_holds_is_refused_rather_than_open_to_all()
    {
        (await Guard().EnsureCanWorkAsync(MakeLead(), CancellationToken.None))
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task An_M12_outage_fails_CLOSED()
    {
        // The opposite of the capture path, deliberately: this decides whether somebody may edit
        // another person's lead, and an outage is not a reason to widen that.
        _admin.GetTeamAgentIdsAsync(Tenant, _me, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Guid>>>(_ => throw new HttpRequestException("M12 down"));

        var result = await Guard().EnsureCanWorkAsync(
            MakeLead(assignedId: _holder), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(LeadAccessGuard.Forbidden);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        (await Guard(callerId: Guid.Empty).EnsureCanWorkAsync(
                MakeLead(assignedId: _holder), CancellationToken.None))
            .IsFailure.Should().BeTrue();
    }
}
