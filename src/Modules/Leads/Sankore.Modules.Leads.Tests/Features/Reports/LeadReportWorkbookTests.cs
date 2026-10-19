namespace Sankore.Modules.Leads.Tests.Features.Reports;

using ClosedXML.Excel;
using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Reports.GetLeadReport;
using Xunit;

/// <summary>
/// An export is only useful if Excel can compute with it: numbers must be numbers, rates fractions
/// under a percent format, and an absent figure an empty cell rather than a misleading zero.
/// </summary>
public sealed class LeadReportWorkbookTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static LeadReportDto Report() => new(
        From, From.AddDays(2), ReportGranularity.Day,
        new LeadReportKpis(Captured: 3, ConvertedInPeriod: 1, CohortConversionRate: 33.3, Lost: 0,
            OpenLeads: 2, AvgFirstContactMinutes: null, SlaComplianceRate: 50, OpenSlaBreaches: 1),
        [new LeadReportPoint(From, 2, 0), new LeadReportPoint(From.AddDays(1), 1, 1)],
        [new LeadReportStage(PipelineStage.New, 2), new LeadReportStage(PipelineStage.Converted, 1)],
        [new LeadReportStatus(LeadStatus.New, 2)],
        [new LeadReportSource(LeadSource.Web, 3, 1, 33.3)],
        [new LeadReportAgent(Guid.NewGuid(), "Awa Sangaré", 2, 1, 0, 1, 50, null, 1, 50, 1)],
        [new LeadReportAgency(null, "Sans agence", 3, 1, 33.3)]);

    [Fact]
    public void Every_block_of_the_screen_has_its_sheet()
    {
        using var wb = Open(LeadReportWorkbook.Build(Report(), agencyName: null, DateTimeOffset.UtcNow));

        wb.Worksheets.Select(w => w.Name).Should().Equal(
            "Synthèse", "Évolution", "Progression", "Statuts", "Canaux", "Agents", "Agences");
    }

    [Fact]
    public void Rates_are_fractions_and_a_missing_figure_is_an_empty_cell()
    {
        using var wb = Open(LeadReportWorkbook.Build(Report(), agencyName: null, DateTimeOffset.UtcNow));
        var agents = wb.Worksheet("Agents");

        agents.Cell(2, 1).GetString().Should().Be("Awa Sangaré");
        agents.Cell(2, 6).GetDouble().Should().Be(0.5);
        agents.Cell(2, 6).Style.NumberFormat.Format.Should().Be("0.0%");
        // No first contact yet: blank, not "0 minutes".
        agents.Cell(2, 7).IsEmpty().Should().BeTrue();
    }

    [Fact]
    public void The_series_holds_real_dates_and_french_labels()
    {
        using var wb = Open(LeadReportWorkbook.Build(Report(), agencyName: "Agence 2", DateTimeOffset.UtcNow));

        wb.Worksheet("Évolution").Cell(2, 1).DataType.Should().Be(XLDataType.DateTime);
        wb.Worksheet("Progression").Cell(3, 1).GetString().Should().Be("Converti");
        wb.Worksheet("Synthèse").Cell(5, 2).GetString().Should().Be("Agence 2");
    }

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));
}
