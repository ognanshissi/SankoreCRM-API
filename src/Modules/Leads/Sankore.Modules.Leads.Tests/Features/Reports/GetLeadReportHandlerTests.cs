namespace Sankore.Modules.Leads.Tests.Features.Reports;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Reports.GetLeadReport;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The report's figures each answer ONE question, and the questions differ on purpose: a
/// conversion counts in the period it HAPPENED, the cohort rate is about the leads CAPTURED in the
/// period. These tests pin those two apart, because merging them is the easy mistake.
/// </summary>
public sealed class GetLeadReportHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    // A Wednesday, so the week bucket has to go back to Monday the 5th.
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    public GetLeadReportHandlerTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_agentId] = "Awa Sangaré" });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Conversions_count_in_the_period_they_happen_and_the_cohort_rate_in_the_capture_period()
    {
        // Captured BEFORE the period, converted inside it: an activity conversion, not a cohort one.
        await AddLeadAsync(capturedAt: From.AddDays(-20), convertedAt: From.AddDays(2));
        // Captured inside, converted inside.
        await AddLeadAsync(capturedAt: From.AddDays(1), convertedAt: From.AddDays(3));
        // Captured inside, still open.
        await AddLeadAsync(capturedAt: From.AddDays(4));
        await AddLeadAsync(capturedAt: From.AddDays(4));

        var report = await RunAsync(ReportGranularity.Day);

        report.Kpis.Captured.Should().Be(3);
        report.Kpis.ConvertedInPeriod.Should().Be(2);
        report.Kpis.CohortConversionRate.Should().Be(33.3);
    }

    [Fact]
    public async Task Projects_opened_in_the_period_are_counted_by_their_phase_today()
    {
        // Projects, lot 4: the application stages left the lead pipeline for the projects.
        await using (var db = _db.CreateContext())
        {
            var inside = new FixedClock(From.AddDays(2));
            var won = Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Crédit", "CREDCOM", null, inside,
                estimatedAmount: new Sankore.Shared.Kernel.ValueObject.Money(500_000m, "XOF"));
            won.Close(OpportunityStage.ClosedWon, From.AddDays(3));
            db.Opportunities.Add(won);
            db.Opportunities.Add(Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Épargne", "EPARGNE", null, inside));
            // Opened before the period: not in this report.
            db.Opportunities.Add(Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Ancien", "", null, new FixedClock(From.AddDays(-5))));
            await db.SaveChangesAsync();
        }

        var report = await RunAsync(ReportGranularity.Day);

        report.Projects!.Single(p => p.Phase == OpportunityStage.ClosedWon).Should().Be(
            new LeadReportProjectPhase(OpportunityStage.ClosedWon, 1, 500_000m, 500_000m));
        report.Projects!.Single(p => p.Phase == OpportunityStage.Prospecting).Count.Should().Be(1);
        report.Projects!.Sum(p => p.Count).Should().Be(2);
        report.Progression.Select(p => p.Stage).Should().NotContain(PipelineStage.DocumentCollection);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task The_series_has_every_bucket_including_the_empty_ones()
    {
        await AddLeadAsync(capturedAt: From.AddHours(10));
        await AddLeadAsync(capturedAt: From.AddDays(5).AddHours(1));

        var report = await RunAsync(ReportGranularity.Day);

        report.Series.Should().HaveCount(14);
        report.Series[0].Captured.Should().Be(1);
        report.Series[1].Captured.Should().Be(0);
        report.Series[5].Captured.Should().Be(1);
    }

    [Fact]
    public void A_week_bucket_starts_on_monday_utc()
    {
        var wednesday = new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.Zero);

        GetLeadReportHandler.Bucket(wednesday, ReportGranularity.Week)
            .Should().Be(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task An_agent_row_counts_the_assignments_of_the_period_and_its_conversions_in_it()
    {
        var onTime = await AddLeadAsync(capturedAt: From.AddDays(1));
        var late = await AddLeadAsync(capturedAt: From.AddDays(1));
        var open = await AddLeadAsync(capturedAt: From.AddDays(2));

        await AssignAsync(onTime, createdAt: From.AddDays(1), slaHours: 4, contactAfterHours: 1);
        await AssignAsync(late, createdAt: From.AddDays(1), slaHours: 4, contactAfterHours: 6);
        await AssignAsync(open, createdAt: From.AddDays(2), slaHours: 4, contactAfterHours: null);
        // Converted after being assigned, as in real life: a closed lead cannot be assigned.
        await ConvertAsync(onTime, From.AddDays(6));

        var report = await RunAsync(ReportGranularity.Day);

        var agent = report.Agents.Should().ContainSingle().Subject;
        agent.Name.Should().Be("Awa Sangaré");
        agent.Assigned.Should().Be(3);
        agent.ContactedWithinSla.Should().Be(1);
        agent.ContactedLate.Should().Be(1);
        agent.NotContacted.Should().Be(1);
        agent.SlaComplianceRate.Should().Be(33.3);
        agent.Converted.Should().Be(1);
        // The converted lead is no longer workload; the two others are.
        agent.OpenLeads.Should().Be(2);
    }

    [Fact]
    public async Task A_range_longer_than_the_limit_is_refused()
    {
        var handler = new GetLeadReportHandler(_db.CreateContext(), new FixedTenantContext(_tenantId), _admin, TimeProvider.System);

        var result = await handler.Handle(
            new GetLeadReportQuery(From, From.AddDays(GetLeadReportHandler.MaxRangeDays + 1), null, null), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("REPORT_RANGE_TOO_LONG");
    }

    // ── Forecast ────────────────────────────────────────────────────────────

    private Opportunity Project(decimal amount, DateTimeOffset closeOn, OpportunityStage stage = OpportunityStage.Prospecting)
    {
        var p = Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Projet", "", null, TimeProvider.System,
            estimatedAmount: new Sankore.Shared.Kernel.ValueObject.Money(amount, "XOF"), expectedCloseDate: closeOn);
        if (stage != OpportunityStage.Prospecting)
            p.AdvanceStage(stage, From);
        return p;
    }

    private async Task<LeadReportForecast> ForecastAsync(bool tracksForecast = true)
    {
        await using (var db = _db.CreateContext())
        {
            if (!tracksForecast)
                db.ProjectTemplates.Add(ProjectTemplate.Create(_tenantId, "Standard", null, null,
                    [new(null, "Vente", OpportunityStage.Prospecting, 50)], From, tracksForecast: false).Value!);

            db.Opportunities.AddRange(
                Project(1_000_000m, From.AddDays(5), OpportunityStage.Negotiation), // in the period, 80 %
                Project(500_000m, From.AddDays(-3)),                                // late, 10 %
                Project(2_000_000m, To.AddDays(10)),                                // after the period
                Project(3_000_000m, From.AddDays(2), OpportunityStage.ClosedWon));  // already won
            await db.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        var handler = new GetLeadReportHandler(
            read, new FixedTenantContext(_tenantId), _admin, new FixedClock(From),
            new Sankore.Modules.Leads.Features.ProjectTemplates.ProjectTemplateResolver(read, _admin, new FixedTenantContext(_tenantId)));
        var result = await handler.Handle(new GetLeadReportQuery(From, To, null, ReportGranularity.Day), default);
        return result.Value!.Forecast!;
    }

    [Fact]
    public async Task The_forecast_weighs_the_open_projects_expected_to_close_in_the_period()
    {
        var forecast = await ForecastAsync();

        forecast.Count.Should().Be(1);
        forecast.Amount.Should().Be(1_000_000m);
        forecast.WeightedAmount.Should().Be(800_000m, "1 000 000 × 80 % at the negotiation step");
        forecast.Overdue.Should().Be(1, "a close date already passed is a project to chase");
        forecast.OverdueWeightedAmount.Should().Be(50_000m);
    }

    [Fact]
    public async Task A_template_that_tracks_no_forecast_is_left_out()
    {
        // A product sold in a meeting: its close date and its 10 % would be noise in the forecast.
        var forecast = await ForecastAsync(tracksForecast: false);

        forecast.Count.Should().Be(0);
        forecast.Overdue.Should().Be(0);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private async Task<LeadReportDto> RunAsync(ReportGranularity granularity)
    {
        var handler = new GetLeadReportHandler(_db.CreateContext(), new FixedTenantContext(_tenantId), _admin, TimeProvider.System);
        var result = await handler.Handle(new GetLeadReportQuery(From, To, null, granularity), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private async Task<Lead> AddLeadAsync(DateTimeOffset capturedAt, DateTimeOffset? convertedAt = null)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        // Capture stamps "now"; a report needs leads that arrived on given days.
        SetPrivate(lead, nameof(Lead.CapturedAt), capturedAt);
        if (convertedAt is { } at)
        {
            lead.Convert(Guid.NewGuid()).IsSuccess.Should().BeTrue();
            SetPrivate(lead, nameof(Lead.ConvertedAt), (DateTimeOffset?)at);
        }
        lead.ClearDomainEvents();

        await using var db = _db.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task AssignAsync(Lead lead, DateTimeOffset createdAt, int slaHours, int? contactAfterHours)
    {
        var assignment = LeadAssignment.Create(
            _tenantId, lead.Id, _agentId, DispatchingStrategy.RoundRobin, 50,
            slaDeadline: createdAt.AddHours(slaHours), createdAt: createdAt);
        if (contactAfterHours is { } h)
            assignment.RecordFirstContact(createdAt.AddHours(h), FirstContactSource.AgentDeclared);

        await using var db = _db.CreateContext();
        var tracked = await db.Leads.AsTracking().FirstAsync(l => l.Id == lead.Id);
        tracked!.AssignTo(assignment, currentAssignment: null).IsSuccess.Should().BeTrue();
        tracked.ClearDomainEvents();
        db.LeadAssignments.Add(assignment);
        await db.SaveChangesAsync();
    }

    private async Task ConvertAsync(Lead lead, DateTimeOffset at)
    {
        await using var db = _db.CreateContext();
        var tracked = await db.Leads.AsTracking().FirstAsync(l => l.Id == lead.Id);
        tracked!.Convert(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        SetPrivate(tracked, nameof(Lead.ConvertedAt), (DateTimeOffset?)at);
        tracked.ClearDomainEvents();
        await db.SaveChangesAsync();
    }

    private static void SetPrivate<T>(object target, string property, T value)
        => target.GetType().GetProperty(property)!.SetValue(target, value);
}
