namespace Sankore.Modules.Leads.Tests.Features.Import;

using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using Sankore.Shared.Kernel;
using Sankore.Modules.Leads.Features.Import.Readers;
using Xunit;

public sealed class FileImportReaderTests
{
    private const string Csv = """
        FullName,PhoneNumber,InterestedProduct,Source,Latitude,Longitude
        Awa Ndiaye,+221771234567,Crédit individuel,Web,14.6928,-17.4467
        Moussa Diop,+221770000000,Épargne,Agency,,
        """;

    [Fact]
    public async Task Csv_rows_are_read_by_header_name()
    {
        var reader = new FileImportReader(new FakeFileStore(Csv, "leads.csv"));

        var rows = await reader.ReadAsync("leads.csv", CancellationToken.None);

        rows.Should().HaveCount(2);
        rows[0].FullName.Should().Be("Awa Ndiaye");
        rows[0].Source.Should().Be("Web");
        rows[0].Latitude.Should().Be("14.6928");
        // Columns absent from the file come back null for the parser to resolve.
        rows[0].Email.Should().BeNull();
    }

    [Fact]
    public async Task Excel_rows_are_read_by_header_name_and_blanks_become_null()
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet("Sheet1");
        ws.Cell(1, 1).Value = "FullName";
        ws.Cell(1, 2).Value = "PhoneNumber";
        ws.Cell(1, 3).Value = "InterestedProduct";
        ws.Cell(1, 4).Value = "Latitude";
        ws.Cell(2, 1).Value = "Awa Ndiaye";
        ws.Cell(2, 2).Value = "+221771234567";
        ws.Cell(2, 3).Value = "Crédit individuel";
        ws.Cell(3, 1).Value = "Moussa Diop";
        ws.Cell(3, 2).Value = "+221770000000";
        ws.Cell(3, 3).Value = "Épargne";

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);

        var reader = new FileImportReader(new FakeFileStore(ms.ToArray(), "leads.xlsx"));

        var rows = await reader.ReadAsync("leads.xlsx", CancellationToken.None);

        rows.Should().HaveCount(2);
        rows[0].FullName.Should().Be("Awa Ndiaye");
        rows[0].InterestedProduct.Should().Be("Crédit individuel");
        rows[1].Latitude.Should().BeNull();
        rows[1].Email.Should().BeNull();
    }

    [Fact]
    public async Task An_excel_file_with_only_a_header_row_yields_nothing()
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet("Sheet1");
        ws.Cell(1, 1).Value = "FullName";

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);

        var rows = await new FileImportReader(new FakeFileStore(ms.ToArray(), "empty.xlsx"))
            .ReadAsync("empty.xlsx", CancellationToken.None);

        rows.Should().BeEmpty();
    }

    private sealed class FakeFileStore(byte[] content, string reference) : IFileStore
    {
        public FakeFileStore(string content, string reference)
            : this(Encoding.UTF8.GetBytes(content), reference) { }

        public Task<string> StoreAsync(Stream c, string originalFileName, CancellationToken ct)
            => Task.FromResult(reference);

        public Task<Stream> ReadAsync(string fileReference, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(content));

        public Task DeleteAsync(string fileReference, CancellationToken ct) => Task.CompletedTask;
    }
}
