namespace Sankore.Modules.Leads.Tests.Features.AgentObjectives;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.AgentObjectives;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// « Ma journée » redesign, lot 1: a manager sets each agent's monthly objectives (first contacts
/// within 24 h, conversions); a month with none reads the latest earlier one; a past month is closed;
/// an agent outside the manager's team is unknown.
/// </summary>
public sealed class AgentObjectivesTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _otherAgent = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public AgentObjectivesTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _admin.GetTeamAgentIdsAsync(_tenant, _manager, Arg.Any<CancellationToken>())
            .Returns([_agent, _otherAgent, Guid.NewGuid() /* a cashier of the team */]);
        _admin.ListActiveUsersInRolesAsync(_tenant, Arg.Any<IReadOnlyCollection<string>>(), null, Arg.Any<CancellationToken>())
            .Returns([_agent, _otherAgent, _stranger]);
        _admin.GetUserNamesAsync(_tenant, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_agent] = "Amadou Koné", [_otherAgent] = "Awa Traoré" });
    }

    public void Dispose() => _factory.Dispose();

    private ICurrentUser User(Guid id)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(id);
        user.TenantId.Returns(_tenant);
        return user;
    }

    private async Task<Sankore.Shared.Kernel.Result<AgentObjectiveDto>> SetAsync(Guid agent, string month, int? firstContact, int? conversions)
    {
        await using var db = _factory.CreateContext();
        return await new SetAgentObjectiveHandler(db, User(_manager), _admin, _clock)
            .Handle(new SetAgentObjectiveCommand(agent, month, firstContact, conversions), CancellationToken.None);
    }

    private async Task<IReadOnlyList<AgentObjectiveDto>> ListAsync(string? month)
    {
        await using var db = _factory.CreateContext();
        var result = await new ListAgentObjectivesHandler(db, User(_manager), _admin, _clock)
            .Handle(new ListAgentObjectivesQuery(month), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private async Task<AgentObjectiveDto> MineAsync(string? month)
    {
        await using var db = _factory.CreateContext();
        var result = await new GetMyObjectiveHandler(db, User(_agent), _clock)
            .Handle(new GetMyObjectiveQuery(month), CancellationToken.None);
        return result.Value!;
    }

    [Fact]
    public async Task The_team_is_its_agents_only_with_or_without_an_objective()
    {
        await SetAsync(_agent, "2026-10", 80, 12);

        var list = await ListAsync("2026-10");

        list.Select(o => o.AgentId).Should().BeEquivalentTo([_agent, _otherAgent]);
        list.Single(o => o.AgentId == _agent).Should().BeEquivalentTo(new
        {
            AgentName = "Amadou Koné", Month = "2026-10", FirstContactWithin24hPercent = 80, Conversions = 12, SetFor = "2026-10",
        });
        list.Single(o => o.AgentId == _otherAgent).SetFor.Should().BeNull();
    }

    [Fact]
    public async Task A_month_without_an_objective_reads_the_latest_earlier_one()
    {
        await SetAsync(_agent, "2026-10", 80, 12);
        await SetAsync(_agent, "2027-01", 85, 15);

        (await MineAsync("2026-12")).Should().BeEquivalentTo(new { Month = "2026-12", FirstContactWithin24hPercent = 80, Conversions = 12, SetFor = "2026-10" });
        (await MineAsync("2027-02")).Should().BeEquivalentTo(new { FirstContactWithin24hPercent = 85, Conversions = 15, SetFor = "2027-01" });
        (await MineAsync("2026-09")).SetFor.Should().BeNull();
    }

    [Fact]
    public async Task The_agents_own_objective_defaults_to_the_current_month()
        => (await MineAsync(null)).Month.Should().Be("2026-10");

    [Fact]
    public async Task Setting_a_month_again_updates_its_row_and_nulls_stop_an_earlier_objective()
    {
        await SetAsync(_agent, "2026-10", 80, 12);
        await SetAsync(_agent, "2026-10", 75, 10);
        await SetAsync(_agent, "2026-12", null, null);

        await using var db = _factory.CreateContext();
        (await db.AgentObjectives.CountAsync()).Should().Be(2);
        (await MineAsync("2026-11")).Should().BeEquivalentTo(new { FirstContactWithin24hPercent = 75, Conversions = 10 });
        (await MineAsync("2027-03")).Should().BeEquivalentTo(new { FirstContactWithin24hPercent = (int?)null, Conversions = (int?)null, SetFor = "2026-12" });
    }

    [Fact]
    public async Task One_agents_plan_reads_three_months_ahead_and_what_was_set()
    {
        await SetAsync(_agent, "2026-10", 80, 12);
        await SetAsync(_agent, "2026-12", 85, 14);

        await using var db = _factory.CreateContext();
        var plan = (await new GetAgentObjectivePlanHandler(db, User(_manager), _admin, _clock)
            .Handle(new GetAgentObjectivePlanQuery(_agent), CancellationToken.None)).Value!;

        plan.AgentName.Should().Be("Amadou Koné");
        plan.Upcoming.Select(u => (u.Month, u.FirstContactWithin24hPercent, u.SetFor)).Should().Equal(
            ("2026-10", 80, "2026-10"), ("2026-11", 80, "2026-10"), ("2026-12", 85, "2026-12"));
        plan.History.Select(h => (h.Month, h.Conversions)).Should().Equal(("2026-12", 14), ("2026-10", 12));
    }

    [Fact]
    public async Task Another_teams_agent_has_no_plan()
    {
        await using var db = _factory.CreateContext();
        (await new GetAgentObjectivePlanHandler(db, User(_manager), _admin, _clock)
            .Handle(new GetAgentObjectivePlanQuery(_stranger), CancellationToken.None))
            .Error.Should().Be(AgentObjectiveErrors.AgentNotFound);
    }

    [Fact]
    public async Task A_past_month_is_closed()
        => (await SetAsync(_agent, "2026-09", 80, 12)).Error.Should().Be(AgentObjectiveErrors.MonthClosed);

    [Fact]
    public async Task An_agent_outside_the_team_is_unknown()
        => (await SetAsync(_stranger, "2026-10", 80, 12)).Error.Should().Be(AgentObjectiveErrors.AgentNotFound);

    [Theory]
    [InlineData(101, 12)]
    [InlineData(-1, 12)]
    [InlineData(80, -1)]
    [InlineData(80, AgentObjective.MaxConversions + 1)]
    public async Task Targets_out_of_range_are_refused(int firstContact, int conversions)
        => (await SetAsync(_agent, "2026-10", firstContact, conversions)).Error.Should().Be(AgentObjectiveErrors.TargetInvalid);

    [Theory]
    [InlineData("2026-13")]
    [InlineData("octobre")]
    [InlineData("2026-10-01")]
    public async Task A_month_is_written_yyyy_mm(string month)
        => (await SetAsync(_agent, month, 80, 12)).Error.Should().Be(AgentObjectiveErrors.MonthInvalid);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
