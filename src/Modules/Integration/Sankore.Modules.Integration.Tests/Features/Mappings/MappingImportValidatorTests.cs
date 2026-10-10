namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using FluentAssertions;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Xunit;

/// <summary>
/// The validator is the whole of criterion 2's "report of rejected lines". It is pure, so these
/// are the cheapest tests in the suite and the ones that have to be exhaustive.
/// </summary>
public sealed class MappingImportValidatorTests
{
    private static MappingImportRow Row(
        int rowNumber, string? crmCode, string? externalCode, string? label = null)
        => new() { RowNumber = rowNumber, CrmCode = crmCode, ExternalCode = externalCode, Label = label };

    [Fact]
    public void A_complete_line_is_valid()
    {
        var report = new MappingImportValidator().Validate([Row(2, "CNI", "ID_CARD", "Carte nationale")]);

        report.TotalRows.Should().Be(1);
        report.ValidRows.Should().Be(1);
        report.InvalidRows.Should().Be(0);
        report.Rows.Single().IsValid.Should().BeTrue();
        report.Rows.Single().Errors.Should().BeEmpty();
    }

    [Fact]
    public void The_label_is_optional()
    {
        var report = new MappingImportValidator().Validate([Row(2, "CNI", "ID_CARD")]);

        report.ValidRows.Should().Be(1);
    }

    [Fact]
    public void A_missing_crm_code_is_reported_by_column_name()
    {
        var report = new MappingImportValidator().Validate([Row(2, "   ", "ID_CARD")]);

        var row = report.Rows.Single();
        row.IsValid.Should().BeFalse();
        row.RowNumber.Should().Be(2);
        row.Errors.Should().ContainSingle().Which.Should().Contain("crm_code");
    }

    [Fact]
    public void A_missing_external_code_is_refused_rather_than_stored_blank()
    {
        var report = new MappingImportValidator().Validate([Row(2, "CNI", null)]);

        // A blank external code would look configured and send nothing — the delete endpoint is
        // how a translation is removed, and the message says so.
        report.Rows.Single().Errors.Should().ContainSingle()
            .Which.Should().Contain("external_code").And.Contain("Delete the mapping");
    }

    [Fact]
    public void An_empty_line_collects_both_errors()
    {
        var report = new MappingImportValidator().Validate([Row(2, null, null)]);

        report.Rows.Single().Errors.Should().HaveCount(2);
    }

    [Fact]
    public void A_code_longer_than_the_column_is_reported_with_its_length()
    {
        var tooLong = new string('X', 101);

        var report = new MappingImportValidator().Validate([Row(2, tooLong, "ID_CARD")]);

        report.Rows.Single().Errors.Should().ContainSingle()
            .Which.Should().Contain("101").And.Contain("100");
    }

    [Fact]
    public void A_label_longer_than_the_column_is_reported()
    {
        var report = new MappingImportValidator().Validate(
            [Row(2, "CNI", "ID_CARD", new string('L', 201))]);

        report.Rows.Single().Errors.Should().ContainSingle().Which.Should().Contain("201");
    }

    [Fact]
    public void Two_lines_with_the_same_crm_code_are_both_refused_and_both_line_numbers_are_named()
    {
        var report = new MappingImportValidator().Validate(
        [
            Row(2, "CNI", "ID_CARD"),
            Row(7, "CNI", "NATIONAL_ID"),
            Row(8, "PASSPORT", "PASSPORT"),
        ]);

        report.TotalRows.Should().Be(3);
        report.ValidRows.Should().Be(1);
        report.InvalidRows.Should().Be(2);

        // BOTH lines of the collision, not only the second: the file does not say which external
        // code was meant, and importing the last one silently picks for the operator.
        foreach (var row in report.Rows.Where(r => r.CrmCode == "CNI"))
        {
            row.IsValid.Should().BeFalse();
            row.Errors.Should().ContainSingle()
                .Which.Should().Contain("CNI").And.Contain("2").And.Contain("7");
        }

        report.Rows.Single(r => r.CrmCode == "PASSPORT").IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_collision_is_detected_across_surrounding_whitespace()
    {
        var report = new MappingImportValidator().Validate(
        [
            Row(2, "CNI", "ID_CARD"),
            Row(3, " CNI ", "NATIONAL_ID"),
        ]);

        report.ValidRows.Should().Be(0);
    }

    [Fact]
    public void Codes_differing_only_in_case_are_two_different_codes()
    {
        var report = new MappingImportValidator().Validate(
        [
            Row(2, "CI", "CIV"),
            Row(3, "ci", "CIV"),
        ]);

        // Ordinal on purpose: the unique index is on the raw value and the domain never
        // case-folds, so these are two rows PostgreSQL will happily hold.
        report.ValidRows.Should().Be(2);
    }

    [Fact]
    public void Several_crm_codes_may_share_one_external_code()
    {
        var report = new MappingImportValidator().Validate(
        [
            Row(2, "TONTINE_A", "SAV001"),
            Row(3, "TONTINE_B", "SAV001"),
        ]);

        // Two CRM products folding onto one CBS product is the documented case, not an error.
        report.ValidRows.Should().Be(2);
    }

    [Fact]
    public void An_empty_file_body_is_an_empty_report()
    {
        var report = new MappingImportValidator().Validate([]);

        report.TotalRows.Should().Be(0);
        report.ValidRows.Should().Be(0);
        report.InvalidRows.Should().Be(0);
    }
}
