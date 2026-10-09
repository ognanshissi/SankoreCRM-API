namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using FluentAssertions;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Xunit;

public sealed class MappingImportReaderTests
{
    private static MappingImportReader ReaderOver(MappingsMemoryFileStore store) => new(store);

    [Fact]
    public async Task Reads_the_three_columns()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(
            "crm_code,external_code,label\nCNI,ID_CARD,Carte nationale\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        file.HasHeaderError.Should().BeFalse();
        var row = file.Rows.Single();
        row.CrmCode.Should().Be("CNI");
        row.ExternalCode.Should().Be("ID_CARD");
        row.Label.Should().Be("Carte nationale");
    }

    [Fact]
    public async Task Stamps_the_physical_line_number()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(
            "crm_code,external_code,label\nCNI,ID_CARD,\nPASSPORT,PASSPORT,\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        // Header is line 1, so the first data line is 2 — the line the operator sees in Excel.
        file.Rows.Select(r => r.RowNumber).Should().Equal(2, 3);
    }

    [Fact]
    public async Task A_missing_required_column_fails_the_whole_file_and_names_it()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,label\nCNI,Carte\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        file.HasHeaderError.Should().BeTrue();
        file.HeaderError.Should().Contain("external_code");
        file.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_file_is_a_header_error()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(string.Empty);

        var file = await ReaderOver(store).ReadAsync(reference, default);

        file.HasHeaderError.Should().BeTrue();
        file.HeaderError.Should().Contain("header");
    }

    [Fact]
    public async Task A_header_only_file_reads_as_zero_rows()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        file.HasHeaderError.Should().BeFalse();
        file.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_byte_order_mark_is_consumed_not_read_into_the_first_column_name()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(MappingsTestFixtures.WithBom(
            "crm_code,external_code,label\nCNI,ID_CARD,\n"));

        var file = await ReaderOver(store).ReadAsync(reference, default);

        // The export writes a BOM for Excel's sake, so the likeliest file to be re-imported is
        // one we wrote. Without detectEncodingFromByteOrderMarks the first header reads as
        // "﻿crm_code" and the file is rejected for a column it plainly has.
        file.HasHeaderError.Should().BeFalse();
        file.Rows.Single().CrmCode.Should().Be("CNI");
    }

    [Fact]
    public async Task Unknown_columns_are_ignored()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(
            "crm_code,external_code,label,notes\nCNI,ID_CARD,Carte,a verifier\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        // An operator's working file carries notes columns of its own.
        file.HasHeaderError.Should().BeFalse();
        file.Rows.Single().CrmCode.Should().Be("CNI");
    }

    [Fact]
    public async Task Columns_may_be_in_any_order()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("label,external_code,crm_code\nCarte,ID_CARD,CNI\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        file.Rows.Single().ExternalCode.Should().Be("ID_CARD");
    }

    [Fact]
    public async Task Values_are_trimmed()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\n  CNI  ,  ID_CARD  ,\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        file.Rows.Single().CrmCode.Should().Be("CNI");
    }

    [Fact]
    public async Task Blank_lines_are_skipped_rather_than_reported()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(
            "crm_code,external_code,label\nCNI,ID_CARD,\n,,\n\nPASSPORT,PASSPORT,\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        // Spreadsheets accumulate empty lines under real data; they are not the operator's doing.
        file.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_line_missing_its_last_field_is_a_row_not_an_exception()
    {
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\nCNI,ID_CARD\n");

        var file = await ReaderOver(store).ReadAsync(reference, default);

        // MissingFieldFound = null: a short line is something the validator reports, not
        // something that aborts the file before the operator sees any of it.
        file.Rows.Should().HaveCount(1);
        file.Rows.Single().Label.Should().BeNull();
    }
}
