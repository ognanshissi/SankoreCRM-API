namespace Sankore.Modules.Leads.Tests.Features.GetMyPerformance;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetMyPerformance;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// A manager's « Ma journée » (2026-11-03): the team's day within the manager's perimeter — a branch
/// manager their agencies, an administrator every one — and nothing outside it.
/// </summary>
public sealed class TeamScorecardTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly Guid _subBranch = Guid.NewGuid();
    private readonly Guid _otherBranch = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IAgencyScopeProvider _scope = Substitute.For<IAgencyScopeProvider>();

    public TeamScorecardTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _scope.GetAccessibleAgencyIdsAsync(_tenant, _manager, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { _branch, _subBranch });
        _admin.GetTeamRosterAsync(_tenant, Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null), Arg.Any<CancellationToken>())
            .Returns(new TeamRoster(
                [Agency(_branch, "Agence 2", null), Agency(_subBranch, "Guichet Cocody", _branch)],
                [new TeamMember(_agent, "Awa Konan", _subBranch)]));
        _admin.GetTeamRosterAsync(_tenant, null, Arg.Any<CancellationToken>())
            .Returns(new TeamRoster(
                [Agency(_branch, "Agence 2", null), Agency(_subBranch, "Guichet Cocody", _branch), Agency(_otherBranch, "Agence 5", null)],
                [new TeamMember(_agent, "Awa Konan", _subBranch), new TeamMember(_stranger, "Yao Kouassi", _otherBranch)]));
    }

    public void Dispose() => _factory.Dispose();

    private static AgencySummary Agency(Guid id, string name, Guid? parent) => new(id, name, name, parent, true);

    [Fact]
    public async Task A_branch_manager_reads_the_day_of_the_agents_of_their_agencies()
    {
        await SeedAsync();

        var card = (await HandleAsync(new GetTeamScorecardQuery("day", null, null), "BranchManager")).Value!;

        card.AllAgencies.Should().BeFalse();
        card.Agencies.Select(a => a.Name).Should().Equal("Agence 2", "Guichet Cocody");
        card.Captured.Should().Be(1, "the lead captured in another branch is not theirs");
        card.Assigned.Total.Should().Be(1);
        card.Tasks.Should().Be(new TeamTasksDto(1, 1), "a task in progress is started");
        card.LateFirstContacts.Should().Be(1);
        var row = card.Rows.Should().ContainSingle().Subject;
        (row.FullName, row.AgencyName, row.ToContact, row.TasksOverdue).Should().Be(("Awa Konan", "Guichet Cocody", 1, 1));
    }

    [Fact]
    public async Task An_agency_or_agent_outside_the_perimeter_is_refused()
    {
        (await HandleAsync(new GetTeamScorecardQuery("day", _otherBranch, null), "BranchManager")).Error
            .Should().Be(GetTeamScorecardHandler.OutOfScope);
        (await HandleAsync(new GetTeamScorecardQuery("day", null, _stranger), "BranchManager")).Error
            .Should().Be(GetTeamScorecardHandler.OutOfScope);
    }

    [Fact]
    public async Task An_administrator_sees_every_agency_and_an_agency_includes_its_sub_agencies()
    {
        await SeedAsync();

        var all = (await HandleAsync(new GetTeamScorecardQuery("day", null, null), "Administrator")).Value!;
        all.AllAgencies.Should().BeTrue();
        all.Captured.Should().Be(2);
        all.Rows.Should().HaveCount(2);
        await _scope.DidNotReceiveWithAnyArgs().GetAccessibleAgencyIdsAsync(default, default, default);

        var branch = (await HandleAsync(new GetTeamScorecardQuery("day", _branch, null), "Administrator")).Value!;
        branch.Rows.Select(r => r.AgentId).Should().Equal(_agent);
    }

    private async Task<Sankore.Shared.Kernel.Result<TeamScorecardDto>> HandleAsync(GetTeamScorecardQuery query, string role)
    {
        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(_manager);
        me.TenantId.Returns(_tenant);
        me.Roles.Returns([role]);
        await using var read = _factory.CreateContext();
        return await new GetTeamScorecardHandler(read, me, _admin, _scope, new FixedClock(DateTimeOffset.UtcNow.AddSeconds(5)))
            .Handle(query, CancellationToken.None);
    }

    private async Task SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _factory.CreateContext();
        void Assign(Guid agency, Guid agent)
        {
            var lead = LeadTestBuilder.Create().WithTenant(_tenant).WithAgency(agency).Fresh().Build();
            var assignment = LeadAssignment.Create(_tenant, lead.Id, agent, DispatchingStrategy.RoundRobin, 50, now.AddSeconds(-1), now.AddSeconds(-2));
            lead.AssignTo(assignment, null);
            db.Leads.Add(lead);
            db.LeadAssignments.Add(assignment);
        }
        Assign(_subBranch, _agent);
        Assign(_otherBranch, _stranger);

        db.CrmTasks.Add(CrmTask.Create(_tenant, CrmTaskType.Generic, CrmTaskPriority.Medium, "Rappeler", now.AddHours(-1), assignedAgentId: _agent));
        var started = CrmTask.Create(_tenant, CrmTaskType.Generic, CrmTaskPriority.Medium, "En cours", now.AddHours(2), assignedAgentId: _agent);
        started.StartProgress();
        db.CrmTasks.Add(started);
        await db.SaveChangesAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
