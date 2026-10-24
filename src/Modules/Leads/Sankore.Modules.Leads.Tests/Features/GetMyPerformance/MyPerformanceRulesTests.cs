namespace Sankore.Modules.Leads.Tests.Features.GetMyPerformance;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetMyPerformance;
using Xunit;

public sealed class MyPerformanceRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero); // lundi, S42
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Colleague = Guid.NewGuid();

    private static AssignmentRow Row(Guid agent, Guid? lead = null, double? contactAfterMinutes = null,
        bool converted = false, Guid? holder = null)
    {
        var assignedAt = Now.AddDays(-3);
        return new AssignmentRow(
            agent, lead ?? Guid.NewGuid(), assignedAt,
            contactAfterMinutes is { } m ? assignedAt.AddMinutes(m) : null,
            assignedAt.AddHours(2),
            Guid.NewGuid(),
            converted ? LeadStatus.Converted : LeadStatus.New,
            converted ? Now.AddDays(-1) : null,
            holder ?? agent, "Compte épargne");
    }

    [Theory]
    [InlineData("month", 2026, 10, 1)]
    [InlineData("quarter", 2026, 10, 1)]
    [InlineData("30d", 2026, 9, 13)]
    public void Periods_start_where_the_screen_says(string period, int y, int m, int d)
    {
        var window = GetMyPerformanceHandler.Window(period, Now);

        window!.Value.From.Should().Be(new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero));
        window.Value.To.Should().Be(Now);
    }

    [Fact]
    public void An_unknown_period_is_refused()
        => GetMyPerformanceHandler.Window("year", Now).Should().BeNull();

    [Fact]
    public void A_conversion_counts_for_the_agent_holding_the_lead_when_it_converted()
    {
        var rows = new[]
        {
            Row(Me, contactAfterMinutes: 30, converted: true),
            Row(Me, contactAfterMinutes: 90),
            Row(Me, converted: true, holder: Colleague), // converti après passage à un collègue
            Row(Me),
        };

        var figures = GetMyPerformanceHandler.Figures(rows, [], perAgent: false);

        figures.LeadsHandled.Should().Be(4);
        figures.ConversionRate.Should().Be(25);
        figures.AvgFirstContactMinutes.Should().Be(60);
        figures.DeadlinesRespectedRate.Should().BeNull("aucune tâche n'était échue");
    }

    [Fact]
    public void The_agency_is_averaged_per_agent()
    {
        var rows = new[] { Row(Me), Row(Me), Row(Colleague), Row(Colleague), Row(Colleague), Row(Colleague) };

        GetMyPerformanceHandler.Figures(rows, [], perAgent: true).LeadsHandled.Should().Be(3);
    }

    [Fact]
    public void A_deadline_is_respected_only_when_completed_by_its_due_time()
    {
        var due = Now.AddDays(-2);
        var tasks = new[]
        {
            new TaskRow(Me, null, due, due.AddMinutes(-5)),
            new TaskRow(Me, null, due, due.AddMinutes(5)),
            new TaskRow(Me, null, due, null),
            new TaskRow(Me, null, due, due),
        };

        GetMyPerformanceHandler.Figures([], tasks, perAgent: false).DeadlinesRespectedRate.Should().Be(50);
    }

    [Fact]
    public void Every_iso_week_of_the_period_is_listed_even_when_empty()
    {
        var weeks = GetMyPerformanceHandler.Weeks(
            new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero), Now,
            [new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero)]);

        weeks.Select(w => w.Label).Should().Equal("S37", "S38", "S39", "S40", "S41", "S42");
        weeks.Single(w => w.Label == "S40").Conversions.Should().Be(2);
        weeks.Where(w => w.Label != "S40").Should().OnlyContain(w => w.Conversions == 0);
    }
}
