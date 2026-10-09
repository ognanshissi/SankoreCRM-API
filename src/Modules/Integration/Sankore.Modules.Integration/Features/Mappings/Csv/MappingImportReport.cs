namespace Sankore.Modules.Integration.Features.Mappings.Csv;

/// <summary>
/// The per-line report of a mapping import (INT-04, criterion 2), shared by the real import and
/// by the dry run so an operator compares the same two reports.
///
/// <para>
/// Modelled on M01's <c>ValidateClientImportResponse</c>: the counts first, then every line with
/// its verdict. <see cref="InvalidRows"/> is carried rather than derived so a client that only
/// renders the header does not have to count the list.
/// </para>
/// </summary>
public sealed record MappingImportReport(
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    IReadOnlyList<MappingRowResult> Rows)
{
    internal static MappingImportReport From(IReadOnlyList<MappingRowResult> rows)
    {
        var valid = rows.Count(r => r.IsValid);
        return new MappingImportReport(rows.Count, valid, rows.Count - valid, rows);
    }
}

/// <summary>
/// One line's verdict.
///
/// <para>
/// <see cref="RowNumber"/> is the PHYSICAL line in the file, header included, so an operator can
/// jump straight to it in their spreadsheet. The codes come back as they were typed — nullable,
/// because a line missing its <c>crm_code</c> is exactly the line that must be reported.
/// </para>
///
/// <para>
/// <see cref="Errors"/> are human sentences naming the offending value. A report is pasted into a
/// ticket and read by whoever maintains the correspondence table, not by a program.
/// </para>
/// </summary>
public sealed record MappingRowResult(
    int RowNumber,
    string? CrmCode,
    string? ExternalCode,
    bool IsValid,
    IReadOnlyList<string> Errors);
