namespace Sankore.Modules.Integration.Features.Mappings.Csv;

using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Sankore.Modules.Integration.Domain;

/// <summary>One exported line. The same three columns the import reads, so a round trip works.</summary>
internal sealed record MappingExportRow(string CrmCode, string ExternalCode, string Label);

/// <summary>
/// Explicit map, like M01's client export: the column names and their order are the contract with
/// whatever spreadsheet opens the file, so they cannot be allowed to follow a property rename.
/// </summary>
internal sealed class MappingExportRowMap : ClassMap<MappingExportRow>
{
    public MappingExportRowMap()
    {
        Map(r => r.CrmCode).Index(0).Name(MappingCsvColumns.CrmCode);
        Map(r => r.ExternalCode).Index(1).Name(MappingCsvColumns.ExternalCode);
        Map(r => r.Label).Index(2).Name(MappingCsvColumns.Label);
    }
}

internal static class MappingExportCsv
{
    internal const string ContentType = "text/csv; charset=utf-8";

    internal static string FileName(MappingDomain domain, Guid connectionId)
        => $"integration-mappings-{domain.ToString().ToLowerInvariant()}-{connectionId:N}.csv";

    /// <summary>
    /// UTF-8 WITH a BOM: without it Excel misreads the accented labels of a West-African code
    /// table ("Pièce d'identité", "Côte d'Ivoire") and the operator re-types the whole file.
    /// </summary>
    internal static async Task<byte[]> WriteAsync(
        IReadOnlyCollection<MappingExportRow> rows, CancellationToken ct)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };

        using var buffer = new MemoryStream();

        // leaveOpen: the writer must flush and dispose while the MemoryStream is still readable.
        await using (var textWriter = new StreamWriter(
                         buffer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        await using (var csv = new CsvWriter(textWriter, config))
        {
            csv.Context.RegisterClassMap<MappingExportRowMap>();
            await csv.WriteRecordsAsync(rows, ct);
        }

        return buffer.ToArray();
    }
}
