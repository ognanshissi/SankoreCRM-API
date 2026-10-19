namespace Sankore.Modules.Leads.Tests.Features.Reports;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Reports.GetAgentReport;
using Sankore.Modules.Leads.Features.Reports.GetLeadReport;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// An agent's sheet and their row in the overview must agree to the unit: they are read side by
/// side, and two definitions of "converted" would make the comparison worthless.
/// </summary>
public sealed class GetAgentReportHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _awa = Guid.NewGuid();
    private readonly Guid _koffi = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    public GetAgentReportHandlerTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_awa] = "Awa Sangaré", [_koffi] = "Koffi Yao" });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Two_agents_are_compared_on_the_same_definitions_as_the_overview()
    {
        var a1 = await AddLeadAsync(From.AddDays(1));
        var a2 = await AddLeadAsync(From.AddDays(1));
        var k1 = await AddLeadAsync(From.AddDays(2));

        await AssignAsync(a1, _awa, From.AddDays(1), slaHours: 4, contactAfterHours: 1);
        await AssignAsync(a2, _awa, From.AddDays(1), slaHours: 4, contactAfterHours: 8);
        await AssignAsync(k1, _koffi, From.AddDays(2), slaHours: 4, contactAfterHours: null);
        await ConvertAsync(a1, From.AddDays(5));

        var comparison = await RunAgentsAsync(_awa, _koffi);
        var overview = await RunOverviewAsync();

        var awa = comparison.Agents.Single(a => a.AgentId == _awa);
        awa.Name.Should().Be("Awa Sangaré");
        awa.Assigned.Should().Be(2);
        awa.SlaComplianceRate.Should().Be(50);
        awa.Converted.Should().Be(1);
        awa.ConversionRate.Should().Be(50);
        awa.OpenLeads.Should().Be(1);

        var koffi = comparison.Agents.Single(a => a.AgentId == _koffi);
        koffi.NotContacted.Should().Be(1);
        koffi.Converted.Should().Be(0);

        // The overview's row says exactly the same.
        var row = overview.Agents.Single(a => a.AgentId == _awa);
        (row.Assigned, row.SlaComplianceRate, row.Converted, row.ConversionRate, row.OpenLeads)
            .Should().Be((awa.Assigned, awa.SlaComplianceRate, awa.Converted, awa.ConversionRate, awa.OpenLeads));
    }

    [Fact]
    public async Task The_series_counts_each_event_in_the_bucket_it_happened()
    {
        var lead = await AddLeadAsync(From.AddDays(1));
        await AssignAsync(lead, _awa, From.AddDays(1), slaHours: 48, contactAfterHours: 26);
        await ConvertAsync(lead, From.AddDays(4));

        var awa = (await RunAgentsAsync(_awa)).Agents.Single();

        awa.Series.Should().HaveCount(14);
        awa.Series[1].Assigned.Should().Be(1);
        awa.Series[2].Contacted.Should().Be(1);
        awa.Series[4].Converted.Should().Be(1);
        awa.Progression.Single(s => s.Stage == PipelineStage.Converted).Count.Should().Be(1);
    }

    [Fact]
    public async Task No_agent_or_more_than_four_is_refused()
    {
        var handler = Handler();

        (await handler.Handle(new GetAgentReportQuery([], From, To, null, null), default))
            .Error.Should().Be("REPORT_AGENTS_INVALID");
        (await handler.Handle(new GetAgentReportQuery(
                Enumerable.Range(0, GetAgentReportHandler.MaxAgents + 1).Select(_ => Guid.NewGuid()).ToList(), From, To, null, null), default))
            .Error.Should().Be("REPORT_AGENTS_INVALID");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private GetAgentReportHandler Handler() =>
        new(_db.CreateContext(), new FixedTenantContext(_tenantId), _admin, TimeProvider.System);

    private async Task<AgentComparisonDto> RunAgentsAsync(params Guid[] agents)
    {
        var result = await Handler().Handle(new GetAgentReportQuery(agents, From, To, null, ReportGranularity.Day), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private async Task<LeadReportDto> RunOverviewAsync()
    {
        var handler = new GetLeadReportHandler(_db.CreateContext(), new FixedTenantContext(_tenantId), _admin, TimeProvider.System);
        var result = await handler.Handle(new GetLeadReportQuery(From, To, null, ReportGranularity.Day), default);
        return result.Value!;
    }

    private async Task<Lead> AddLeadAsync(DateTimeOffset capturedAt)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        SetPrivate(lead, nameof(Lead.CapturedAt), capturedAt);
        lead.ClearDomainEvents();
        await using var db = _db.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task AssignAsync(Lead lead, Guid agentId, DateTimeOffset createdAt, int slaHours, int? contactAfterHours)
    {
        var assignment = LeadAssignment.Create(
            _tenantId, lead.Id, agentId, DispatchingStrategy.RoundRobin, 50,
            slaDeadline: createdAt.AddHours(slaHours), createdAt: createdAt);
        if (contactAfterHours is { } h)
            assignment.RecordFirstContact(createdAt.AddHours(h), FirstContactSource.AgentDeclared);

        await using var db = _db.CreateContext();
        var tracked = await db.Leads.AsTracking().FirstAsync(l => l.Id == lead.Id);
        tracked.AssignTo(assignment, currentAssignment: null).IsSuccess.Should().BeTrue();
        tracked.ClearDomainEvents();
        db.LeadAssignments.Add(assignment);
        await db.SaveChangesAsync();
    }

    private async Task ConvertAsync(Lead lead, DateTimeOffset at)
    {
        await using var db = _db.CreateContext();
        var tracked = await db.Leads.AsTracking().FirstAsync(l => l.Id == lead.Id);
        tracked.Convert(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        SetPrivate(tracked, nameof(Lead.ConvertedAt), (DateTimeOffset?)at);
        tracked.ClearDomainEvents();
        await db.SaveChangesAsync();
    }

    private static void SetPrivate<T>(object target, string property, T value)
        => target.GetType().GetProperty(property)!.SetValue(target, value);
}
