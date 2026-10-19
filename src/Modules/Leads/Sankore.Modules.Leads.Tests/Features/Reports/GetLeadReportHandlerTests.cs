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
