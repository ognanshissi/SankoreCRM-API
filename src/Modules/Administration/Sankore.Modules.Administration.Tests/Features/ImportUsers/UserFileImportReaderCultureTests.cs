namespace Sankore.Modules.Administration.Tests.Features.ImportUsers;

using System.Globalization;
using ClosedXML.Excel;
using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Features.ImportUsers;
using Sankore.Modules.Administration.Features.ImportUsers.Readers;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Every column of the user import is text today, so this reader had no live defect — these
/// tests pin the behaviour now that it shares the invariant cell reader, including the one
/// semantic that must NOT change: a blank DefaultLanguage still fails validation.
/// </summary>
public sealed class UserFileImportReaderCultureTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"users-{Guid.NewGuid():N}.xlsx");

    public UserFileImportReaderCultureTests()
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

    private async Task<List<ImportUserRow>> ReadAsync(string[] headers, Action<IXLWorksheet> fill)
    {
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Users");
            for (var i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            fill(ws);
            wb.SaveAs(_path);
        }

        var store = Substitute.For<IFileStore>();
        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(File.OpenRead(_path)));

        return await new FileImportReader(store).ReadAsync("u.xlsx", CancellationToken.None);
    }

    [Fact]
    public async Task An_agency_code_typed_as_a_number_is_not_mangled_by_the_culture()
    {
        var rows = await ReadAsync(
            ["FirstName", "LastName", "Email", "AgencyCode"],
            ws =>
            {
                ws.Cell(2, 1).Value = "Awa";
                ws.Cell(2, 2).Value = "Ouattara";
                ws.Cell(2, 3).Value = "awa@mfi.ci";
                ws.Cell(2, 4).Value = 1042;   // numeric cell, not text
            });

        rows.Should().ContainSingle();
        rows[0].AgencyCode.Should().Be("1042");
    }

    [Fact]
    public async Task A_blank_default_language_stays_blank_so_validation_still_catches_it()
    {
        // Not "fr": the CSV path leaves it empty and the validator reports
        // "DefaultLanguage is required". Excel must not silently disagree.
        var rows = await ReadAsync(
            ["FirstName", "LastName", "Email", "DefaultLanguage"],
            ws =>
            {
                ws.Cell(2, 1).Value = "Awa";
                ws.Cell(2, 2).Value = "Ouattara";
                ws.Cell(2, 3).Value = "awa@mfi.ci";
            });

        rows[0].DefaultLanguage.Should().BeEmpty();
    }

    [Fact]
    public async Task An_absent_default_language_column_falls_back_to_french()
    {
        var rows = await ReadAsync(
            ["FirstName", "LastName", "Email"],
            ws =>
            {
                ws.Cell(2, 1).Value = "Awa";
                ws.Cell(2, 2).Value = "Ouattara";
                ws.Cell(2, 3).Value = "awa@mfi.ci";
            });

        rows[0].DefaultLanguage.Should().Be("fr");
    }

    [Fact]
    public async Task A_row_without_an_email_is_skipped()
    {
        var rows = await ReadAsync(
            ["FirstName", "LastName", "Email"],
            ws =>
            {
                ws.Cell(2, 1).Value = "Awa";
                ws.Cell(2, 2).Value = "Ouattara";
                ws.Cell(3, 1).Value = "Koffi";
                ws.Cell(3, 2).Value = "Kouassi";
                ws.Cell(3, 3).Value = "koffi@mfi.ci";
            });

        rows.Should().ContainSingle();
        rows[0].Email.Should().Be("koffi@mfi.ci");
    }
}
