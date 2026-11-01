namespace Sankore.Modules.Leads.Tests.Features.GetMyPerformance;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.AgentObjectives;
using Sankore.Modules.Leads.Features.GetMyPerformance;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;
using Row = Sankore.Modules.Leads.Features.GetMyPerformance.GetMyScorecardHandler.Row;

/// <summary>
/// « Ma journée » redesign, lot 2: the agent's figures. Each rule is pinned on its own, then the
/// handler once for what only the queries decide (superseded assignments, today's list, objectives).
/// </summary>
public sealed class MyScorecardTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 11, 0, 0, TimeSpan.Zero); // vendredi, S44

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public MyScorecardTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private static Row R(LeadStatus status = LeadStatus.Open, double daysAgo = 3, double? contactAfterHours = null, Guid? lead = null)
    {
        var at = Now.AddDays(-daysAgo);
        return new Row(lead ?? Guid.NewGuid(), at, contactAfterHours is { } h ? at.AddHours(h) : null, at.AddHours(2), null, status);
    }

    [Theory]
    [InlineData("week", 2026, 10, 26)]
    [InlineData("month", 2026, 10, 1)]
    public void The_week_starts_on_monday_and_the_month_on_the_first(string period, int y, int m, int d)
    {
        var window = GetMyScorecardHandler.Window(period, Now)!.Value;

        window.From.Should().Be(new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero));
        window.To.Should().Be(Now);
    }

    [Fact]
    public void An_unknown_period_is_refused()
        => GetMyScorecardHandler.Window("quarter", Now).Should().BeNull();

    [Fact]
    public void Leads_are_counted_once_by_where_they_stand_now()
    {
        var twice = Guid.NewGuid();
        var rows = new[]
        {
            R(LeadStatus.New), R(LeadStatus.New, lead: twice), R(LeadStatus.New, lead: twice),
            R(LeadStatus.Open), R(LeadStatus.Qualified), R(LeadStatus.Nurturing),
            R(LeadStatus.Converted),
            R(LeadStatus.Lost), R(LeadStatus.Disqualified), R(LeadStatus.Archived),
        };

        GetMyScorecardHandler.Assigned(rows).Should().Be(new ScorecardAssignedDto(9, 2, 3, 1, 3));
    }

    [Fact]
    public void A_first_contact_is_on_time_within_24_hours_and_a_fresh_uncontacted_lead_is_not_yet_late()
    {
        var rows = new[]
        {
            R(contactAfterHours: 2),
            R(contactAfterHours: 24),
            R(contactAfterHours: 30),
            R(),                                  // jamais contacté, attribué il y a 3 jours : en retard
            R(daysAgo: 0.5),                      // attribué il y a 12 h, pas encore contacté : ni l'un ni l'autre
        };

        GetMyScorecardHandler.FirstContact(rows, Now, 80).Should().Be(new ScorecardFirstContactDto(50, 2, 4, 80));
    }

    [Fact]
    public void No_eligible_assignment_reads_no_rate()
        => GetMyScorecardHandler.FirstContact([R(daysAgo: 0.2)], Now, null).Rate.Should().BeNull();

    [Fact]
    public void The_last_four_iso_weeks_are_listed_even_when_empty()
    {
        var rows = new[] { R(daysAgo: 0), R(daysAgo: 1), R(daysAgo: 15) };

        var weeks = GetMyScorecardHandler.Weeks(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), rows);

        weeks.Select(w => (w.Label, w.Received)).Should().Equal(("S41", 0), ("S42", 1), ("S43", 0), ("S44", 2));
    }

    [Fact]
    public void Weekend_days_appear_only_when_leads_arrived_or_when_it_is_today()
    {
        var monday = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        var saturday = monday.AddDays(5);
        var rows = new[] { R(daysAgo: (Now - saturday.AddHours(10)).TotalDays) };

        GetMyScorecardHandler.Days(monday, monday.AddDays(4), rows).Select(d => d.Date.DayOfWeek)
            .Should().Equal(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday);
        GetMyScorecardHandler.Days(monday, monday.AddDays(6), []).Last().Should().Be(new ScorecardDayDto(new DateOnly(2026, 11, 1), 0, true));
    }

    [Theory]
    [InlineData(12, 3)]   // 12 × 7 / 31 = 2,7
    [InlineData(1, 1)]    // never rounded down to nothing
    [InlineData(0, 0)]
    public void The_weeks_objective_is_seven_days_of_the_months(int monthly, int weekly)
        => GetMyScorecardHandler.WeeklyShare(monthly, Now).Should().Be(weekly);

    [Fact]
    public async Task The_handler_reads_my_assignments_today_and_my_objective()
    {
        var withdrawn = Guid.NewGuid();
        await using (var db = _factory.CreateContext())
        {
            Lead Assign(string name, DateTimeOffset at, Guid agent, bool supersede = false, LeadSource source = LeadSource.WhatsApp)
            {
                var lead = LeadTestBuilder.Create().WithTenant(_tenant).WithName(name).WithSource(source).Fresh().Build();
                var assignment = LeadAssignment.Create(_tenant, lead.Id, agent, DispatchingStrategy.RoundRobin, 50, at.AddHours(24), at);
                lead.AssignTo(assignment, null);
                if (supersede) assignment.Supersede(at.AddMinutes(5));
                db.Leads.Add(lead);
                db.LeadAssignments.Add(assignment);
                return lead;
            }

            Assign("Céline Anoh", Now.AddHours(-3), _me);
            Assign("Éric Yeo", Now.AddHours(-1), _me, source: LeadSource.Ussd);
            Assign("Retiré avant contact", Now.AddHours(-2), _me, supersede: true);
            Assign("Hier", Now.AddDays(-1), _me);
            Assign("Collègue", Now.AddHours(-1), withdrawn);
            db.AgentObjectives.Add(AgentObjective.Create(_tenant, _me, new DateOnly(2026, 9, 1), 80, 12, Guid.NewGuid(), Now));
            await db.SaveChangesAsync();
        }

        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(_me);
        me.TenantId.Returns(_tenant);

        await using var read = _factory.CreateContext();
        var result = await new GetMyScorecardHandler(read, me, Substitute.For<IAdministrationModule>(), new FixedClock(Now))
            .Handle(new GetMyScorecardQuery("month"), CancellationToken.None);

        var card = result.Value!;
        card.Assigned.Total.Should().Be(3, "the assignment withdrawn before any contact is not the agent's work");
        card.AssignedToday.Total.Should().Be(2);
        card.AssignedToday.Items.Select(i => (i.FullName, i.Source)).Should().Equal(("Céline Anoh", "WhatsApp"), ("Éric Yeo", "Ussd"));
        card.AssignedToday.Items.Should().OnlyContain(i => !i.Contacted && i.Product == "Crédit individuel");
        card.FirstContactWithin24h.Objective.Should().Be(80, "September's objective carries over to October");
        card.Conversions.Objective.Should().Be(12);

        var week = await new GetMyScorecardHandler(read, me, Substitute.For<IAdministrationModule>(), new FixedClock(Now))
            .Handle(new GetMyScorecardQuery("week"), CancellationToken.None);
        week.Value!.Conversions.Objective.Should().Be(3, "12 a month is 3 for seven days of October");
        week.Value.DailyReceived.Select(d => (d.Date.DayOfWeek, d.Received, d.IsToday)).Should().Equal(
            (DayOfWeek.Monday, 0, false), (DayOfWeek.Tuesday, 0, false), (DayOfWeek.Wednesday, 0, false),
            (DayOfWeek.Thursday, 1, false), (DayOfWeek.Friday, 2, true));
        card.DailyReceived.Should().BeEmpty("the month shows weeks, not days");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
