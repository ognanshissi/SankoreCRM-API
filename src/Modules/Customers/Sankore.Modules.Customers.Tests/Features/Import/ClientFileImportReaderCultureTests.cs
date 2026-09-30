namespace Sankore.Modules.Customers.Tests.Features.Import;

using System.Globalization;
using ClosedXML.Excel;
using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Customers.Features.Import;
using Sankore.Modules.Customers.Features.Import.Readers;
using Sankore.Modules.Customers.Features.Import.ValidateImport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Dates are the exposure here: an operator's file stores DateOfBirth and IncorporationDate as
/// date cells, and reading them with the server's culture turns 2 April into 4 February without
/// reporting anything. Runs under fr-FR on purpose.
/// </summary>
public sealed class ClientFileImportReaderCultureTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"clients-{Guid.NewGuid():N}.xlsx");

    public ClientFileImportReaderCultureTests()
    {
        var french = new CultureInfo("fr-FR");
        CultureInfo.CurrentCulture = french;
        CultureInfo.DefaultThreadCurrentCulture = french;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _original;
        CultureInfo.DefaultThreadCurrentCulture = null;
        if (File.Exists(_path)) File.Delete(_path);
    }

    private async Task<ImportClientRow> ReadSingleAsync(Action<IXLWorksheet> fill)
    {
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Clients");
            string[] headers =
            [
                "FirstName", "LastName", "DateOfBirth", "Nationality",
                "IdentityDocumentNumber", "PhoneNumber", "IncorporationDate",
            ];
            for (var i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];

            ws.Cell(2, 1).Value = "Awa";
            ws.Cell(2, 2).Value = "Ouattara";
            ws.Cell(2, 4).Value = "CI";
            ws.Cell(2, 5).Value = "CI0001";
            ws.Cell(2, 6).Value = "+2250708091801";

            fill(ws);
            wb.SaveAs(_path);
        }

        var store = Substitute.For<IFileStore>();
        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(File.OpenRead(_path)));

        var rows = await new ClientFileImportReader(store).ReadAsync("c.xlsx", CancellationToken.None);
        return rows.Should().ContainSingle().Subject;
    }

    [Fact]
    public async Task A_date_of_birth_cell_keeps_the_day_the_operator_typed()
    {
        var row = await ReadSingleAsync(ws => ws.Cell(2, 3).Value = new DateTime(1987, 4, 2));

        row.DateOfBirth.Should().Be("1987-04-02");

        ClientImportValidator.TryParseDate(row.DateOfBirth!, out var parsed).Should().BeTrue();
        parsed.Should().Be(new DateOnly(1987, 4, 2), "4 February would be a silently wrong client");
    }

    [Fact]
    public async Task An_incorporation_date_cell_is_read_the_same_way()
    {
        var row = await ReadSingleAsync(ws => ws.Cell(2, 7).Value = new DateTime(2020, 6, 15));

        row.IncorporationDate.Should().Be("2020-06-15");
    }

    [Fact]
    public async Task A_date_already_written_as_text_still_works()
    {
        var row = await ReadSingleAsync(ws => ws.Cell(2, 3).Value = "1990-04-02");

        row.DateOfBirth.Should().Be("1990-04-02");
    }

    [Fact]
    public async Task A_day_first_date_typed_as_text_is_still_read_day_first()
    {
        // Unchanged behaviour: the validator owns that reading, not the reader.
        var row = await ReadSingleAsync(ws => ws.Cell(2, 3).Value = "02/04/1987");

        row.DateOfBirth.Should().Be("02/04/1987");
        ClientImportValidator.TryParseDate(row.DateOfBirth!, out var parsed).Should().BeTrue();
        parsed.Should().Be(new DateOnly(1987, 4, 2));
    }
}
