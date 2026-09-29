namespace Sankore.Modules.Customers.Tests.Features.Import;

using System.Text;
using FluentAssertions;
using Sankore.Modules.Customers.Features.Import.Readers;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class ClientFileImportReaderTests
{
    /// <summary>Serves a CSV straight from memory; the reader only ever sees a stream.</summary>
    private sealed class InMemoryFileStore(string content) : IFileStore
    {
        public Task<string> StoreAsync(Stream c, string name, CancellationToken ct)
            => Task.FromResult("import.csv");

        public Task<Stream> ReadAsync(string fileReference, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));

        public Task DeleteAsync(string fileReference, CancellationToken ct) => Task.CompletedTask;
    }

    private static Task<List<Sankore.Modules.Customers.Features.Import.ImportClientRow>> ReadAsync(string csv)
        => new ClientFileImportReader(new InMemoryFileStore(csv)).ReadAsync("import.csv", CancellationToken.None);

    [Fact]
    public async Task Reads_the_template_columns()
    {
        var rows = await ReadAsync(
            """
            FirstName,LastName,Gender,DateOfBirth,Nationality,IdentityDocumentNumber,PhoneNumber,Email,AgencyCode
            Awa,Ouattara,Female,1990-04-02,CI,CI0001,+2250708091801,awa@example.ci,ABJ-PLT
            """);

        rows.Should().ContainSingle();
        rows[0].FirstName.Should().Be("Awa");
        rows[0].LastName.Should().Be("Ouattara");
        rows[0].DateOfBirth.Should().Be("1990-04-02");
        rows[0].IdentityDocumentNumber.Should().Be("CI0001");
        rows[0].AgencyCode.Should().Be("ABJ-PLT");
        rows[0].IsLegalEntity.Should().BeFalse();
    }

    [Fact]
    public async Task A_column_the_template_does_not_know_is_ignored_rather_than_fatal()
    {
        // An operator's working file almost always has notes columns of their own.
        var rows = await ReadAsync(
            """
            FirstName,LastName,DateOfBirth,IdentityDocumentNumber,PhoneNumber,Notes interne
            Awa,Ouattara,1990-04-02,CI0001,+2250708091801,à rappeler lundi
            """);

        rows.Should().ContainSingle();
        rows[0].FirstName.Should().Be("Awa");
    }

    [Fact]
    public async Task A_missing_optional_column_is_not_fatal_either()
    {
        var rows = await ReadAsync(
            """
            FirstName,LastName,PhoneNumber
            Awa,Ouattara,+2250708091801
            """);

        rows.Should().ContainSingle();
        rows[0].Nationality.Should().BeNull("the validator decides what is missing, not the reader");
    }

    [Fact]
    public async Task A_row_with_a_legal_name_is_flagged_as_a_company()
    {
        var rows = await ReadAsync(
            """
            LegalName,LegalFormCode,RegistrationNumber,IncorporationDate,PhoneNumber
            Sankore Négoce,SARL,CI-ABJ-2020-B-1234,2020-06-15,+2252722000000
            """);

        rows[0].IsLegalEntity.Should().BeTrue();
        rows[0].LegalName.Should().Be("Sankore Négoce");
        rows[0].IncorporationDate.Should().Be("2020-06-15");
    }

    [Fact]
    public async Task An_empty_file_yields_no_rows_rather_than_an_error()
    {
        var rows = await ReadAsync("FirstName,LastName,PhoneNumber\n");
        rows.Should().BeEmpty();
    }
}
