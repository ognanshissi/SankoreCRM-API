namespace Sankore.Modules.Leads.Tests.Features.Import;

using ClosedXML.Excel;
using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Import;
using Sankore.Modules.Leads.Features.Import.Readers;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Two gaps the import carried: NationalId was not a column at all, and the row's agency was
/// dropped on the way to the capture command.
/// </summary>
public sealed class ImportNationalIdAndAgencyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"leads-{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private async Task<ImportLeadRow> ReadSingleAsync(Action<IXLWorksheet> fill)
    {
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Leads");
            string[] headers = ["FullName", "PhoneNumber", "InterestedProduct", "NationalId", "AgencyId"];
            for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];

            ws.Cell(2, 1).Value = "Awa Ouattara";
            ws.Cell(2, 2).Value = "+2250708091801";
            ws.Cell(2, 3).Value = "Crédit commerçant";
            fill(ws);
            wb.SaveAs(_path);
        }

        var store = Substitute.For<IFileStore>();
        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(File.OpenRead(_path)));

        var rows = await new FileImportReader(store).ReadAsync("f.xlsx", CancellationToken.None);
        return rows.Should().ContainSingle().Subject;
    }

    [Fact]
    public async Task The_reader_picks_up_the_national_id_column()
    {
        var row = await ReadSingleAsync(ws => ws.Cell(2, 4).Value = "CI0123456789");

        row.NationalId.Should().Be("CI0123456789");
    }

    [Fact]
    public async Task The_parser_carries_the_national_id_through()
    {
        var row = await ReadSingleAsync(ws => ws.Cell(2, 4).Value = "CI0123456789");

        var parsed = LeadRowParser.Parse(row, new ImportDefaults(), LeadSource.FileImport);

        parsed.Errors.Should().BeEmpty();
        parsed.Row!.NationalId.Should().Be("CI0123456789");
    }

    [Fact]
    public async Task A_missing_national_id_is_simply_absent()
    {
        var row = await ReadSingleAsync(_ => { });

        row.NationalId.Should().BeNull();

        var parsed = LeadRowParser.Parse(row, new ImportDefaults(), LeadSource.FileImport);
        parsed.Errors.Should().BeEmpty("NationalId is optional");
        parsed.Row!.NationalId.Should().BeNull();
    }

    [Fact]
    public async Task The_row_agency_survives_parsing()
    {
        // ProcessLeadImportJob now passes it as PreferredAgencyId: DispatchLeadHandler scopes its
        // candidate agents with GetAvailableAgentsAsync(tenantId, lead.PreferredAgencyId), so
        // dropping it dispatched imported leads across the whole tenant.
        var agencyId = Guid.NewGuid();
        var row = await ReadSingleAsync(ws => ws.Cell(2, 5).Value = agencyId.ToString());

        var parsed = LeadRowParser.Parse(row, new ImportDefaults(), LeadSource.FileImport);

        parsed.Errors.Should().BeEmpty();
        parsed.Row!.AgencyId.Should().Be(agencyId);
    }
}
