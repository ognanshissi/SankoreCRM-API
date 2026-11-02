namespace Sankore.Modules.Leads.Tests.Features.Reports;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Reports.GetProjectReport;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The « Projets » tab of the reports (reports lot 2, 2026-11-02): what each figure counts, the
/// period before, the time spent per step and the overdue list.
/// </summary>
public sealed class GetProjectReportHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = To;

    public GetProjectReportHandlerTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_ownerId] = "Awa Sangaré" });
    }

    public void Dispose() => _db.Dispose();

    private Opportunity Project(DateTimeOffset createdAt, string product = "Crédit PME", decimal amount = 0,
        DateTimeOffset? expectedClose = null)
        => Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Projet", product, _ownerId, new Clock(createdAt),
            estimatedAmount: amount > 0 ? new Money(amount, "XOF") : null, expectedCloseDate: expectedClose);

    private async Task SaveAsync(params object[] entities)
    {
        await using var db = _db.CreateContext();
        foreach (var e in entities) db.Add(e);
        await db.SaveChangesAsync();
    }

    private async Task<ProjectReportDto> RunAsync()
    {
        var handler = new GetProjectReportHandler(_db.CreateContext(), new FixedTenantContext(_tenantId), _admin, new Clock(Now));
        var result = await handler.Handle(new GetProjectReportQuery(From, To, null), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    [Fact]
    public async Task Won_and_lost_count_in_the_period_they_close_and_are_compared_with_the_period_before()
    {
        var wonNow = Project(From.AddDays(-10));          // opened before, won inside: a 12-day cycle
        wonNow.Close(OpportunityStage.ClosedWon, From.AddDays(2));
        var lostNow = Project(From.AddDays(1));
        lostNow.Close(OpportunityStage.ClosedLost, From.AddDays(3), "Taux trop élevé");
        var wonBefore = Project(From.AddDays(-12));       // won in the previous 14 days
        wonBefore.Close(OpportunityStage.ClosedWon, From.AddDays(-2));
        await SaveAsync(wonNow, lostNow, wonBefore, Project(From.AddDays(4)));

        var report = await RunAsync();

        report.Kpis.Period.Opened.Should().Be(2);
        report.Kpis.Period.Won.Should().Be(1);
        report.Kpis.Period.Lost.Should().Be(1);
        report.Kpis.Period.WinRate.Should().Be(50);
        report.Kpis.Period.AvgCycleDays.Should().Be(12);
        report.Kpis.OpenProjects.Should().Be(1);
        report.Previous.Won.Should().Be(1);
        report.Previous.Opened.Should().Be(2, "both projects opened in the 14 days before From count there");
        report.Previous.WinRate.Should().Be(100);
    }

    [Fact]
    public async Task Loss_reasons_are_grouped_whatever_their_case_and_spaces()
    {
        foreach (var reason in new[] { "Taux trop élevé", " taux TROP élevé ", "Concurrent", null })
        {
            var p = Project(From.AddDays(1));
            p.Close(OpportunityStage.ClosedLost, From.AddDays(2), reason);
            await SaveAsync(p);
        }

        var report = await RunAsync();

        report.LossReasons.Should().HaveCount(3);
        report.LossReasons[0].Should().Be(new ProjectReportLossReason("Taux trop élevé", 2));
        report.LossReasons.Should().Contain(new ProjectReportLossReason("Sans motif", 1));
    }

    [Fact]
    public async Task Overdue_projects_are_listed_the_most_late_first_with_their_owner()
    {
        await SaveAsync(
            Project(From.AddDays(-20), expectedClose: Now.AddDays(-3), amount: 500_000),
            Project(From.AddDays(-20), expectedClose: Now.AddDays(-10)),
            Project(From.AddDays(-20), expectedClose: Now.AddDays(5)));

        var report = await RunAsync();

        report.Kpis.Overdue.Should().Be(2);
        report.Overdue.Select(o => o.DaysLate).Should().Equal(10, 3);
        report.Overdue[0].OwnerName.Should().Be("Awa Sangaré");
        report.Pipeline.Single().Amount.Should().Be(500_000m);
    }

    [Fact]
    public async Task The_time_spent_in_a_step_is_read_off_the_project_s_history()
    {
        var project = Project(From.AddDays(-4));
        // « Analyse » from creation to day 2 (6 days), then « Comité » since, still open.
        await SaveAsync(project,
            ProjectHistoryEntry.Record(project, ProjectHistoryKind.StepChanged, _ownerId, From.AddDays(2), "Analyse", "Comité"));

        var report = await RunAsync();

        var analyse = report.Steps.Single(s => s.Step == "Analyse");
        analyse.Left.Should().Be(1);
        analyse.AvgDays.Should().Be(6);
        var comite = report.Steps.Single(s => s.Step == "Comité");
        comite.WaitingNow.Should().Be(1);
        comite.AvgWaitingDays.Should().Be(12);
    }

    [Fact]
    public async Task The_export_has_one_sheet_per_block_with_the_rates_as_fractions()
    {
        var won = Project(From.AddDays(1));
        won.Close(OpportunityStage.ClosedWon, From.AddDays(2));
        var lost = Project(From.AddDays(1));
        lost.Close(OpportunityStage.ClosedLost, From.AddDays(2), "Concurrent");
        await SaveAsync(won, lost, Project(From.AddDays(-30), expectedClose: Now.AddDays(-1)));

        var bytes = ProjectReportWorkbook.Build(await RunAsync(), null, Now);

        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        wb.Worksheets.Select(w => w.Name).Should().Equal("Synthèse", "Pipeline", "Produits", "Motifs de perte", "Étapes", "En retard");
        wb.Worksheet("Synthèse").Search("Taux de gain").Single().CellRight().GetDouble().Should().Be(0.5);
        wb.Worksheet("En retard").Cell(2, 7).GetDouble().Should().Be(1);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
