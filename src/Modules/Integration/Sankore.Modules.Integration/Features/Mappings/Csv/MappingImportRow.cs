namespace Sankore.Modules.Integration.Features.Mappings.Csv;

using CsvHelper.Configuration;

/// <summary>
/// One line as it was read, every field a <see cref="string"/>.
///
/// <para>
/// All-string deliberately, like the three existing importers: a spreadsheet cell only ever hands
/// back text, and a typed binding aborts the WHOLE file on one bad cell — which is the opposite of
/// a per-line report. Everything that could be wrong with a line is decided by
/// <see cref="MappingImportValidator"/>, in one place.
/// </para>
///
/// <para>
/// <see cref="RowNumber"/> is not a column: the reader stamps it from the parser's physical line
/// so the report can point at the line in the operator's file.
/// </para>
/// </summary>
internal sealed class MappingImportRow
{
    public int RowNumber { get; set; }

    public string? CrmCode { get; set; }

    public string? ExternalCode { get; set; }

    public string? Label { get; set; }

    /// <summary>
    /// Collapses every blank field to <c>null</c> and trims the rest.
    ///
    /// <para>
    /// Called by the reader so the distinction between "the line had no such column" and "the
    /// column was empty" never leaves the boundary: both mean the operator gave no value. The
    /// alternative is every consumer repeating <c>IsNullOrWhiteSpace ? null : Trim()</c>, and the
    /// one that forgets stores an empty string.
    /// </para>
    /// </summary>
    internal void Normalize()
    {
        CrmCode = Blank(CrmCode);
        ExternalCode = Blank(ExternalCode);
        Label = Blank(Label);

        static string? Blank(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Spreadsheets accumulate empty lines under real data; they are not errors.</summary>
    internal bool IsBlank =>
        string.IsNullOrWhiteSpace(CrmCode)
        && string.IsNullOrWhiteSpace(ExternalCode)
        && string.IsNullOrWhiteSpace(Label);
}

/// <summary>
/// Explicit map rather than name inference: the columns are snake_case and fixed by the US, so
/// renaming a C# property must not be able to change the file contract.
/// </summary>
internal sealed class MappingImportRowMap : ClassMap<MappingImportRow>
{
    public MappingImportRowMap()
    {
        Map(r => r.CrmCode).Name(MappingCsvColumns.CrmCode);
        Map(r => r.ExternalCode).Name(MappingCsvColumns.ExternalCode);
        Map(r => r.Label).Name(MappingCsvColumns.Label);

        // Stamped by the reader, never read from the file.
        Map(r => r.RowNumber).Ignore();
    }
}
