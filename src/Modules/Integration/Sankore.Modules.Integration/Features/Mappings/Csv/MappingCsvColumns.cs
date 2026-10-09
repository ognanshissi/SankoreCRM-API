namespace Sankore.Modules.Integration.Features.Mappings.Csv;

/// <summary>
/// The three columns of the mapping file, in order. One declaration shared by the import reader,
/// the export writer and the OpenAPI descriptions: a round trip must be possible, so an export
/// that drifts from what the import accepts is a bug by construction.
/// </summary>
internal static class MappingCsvColumns
{
    internal const string CrmCode = "crm_code";
    internal const string ExternalCode = "external_code";
    internal const string Label = "label";

    internal static readonly string[] All = [CrmCode, ExternalCode, Label];

    /// <summary>
    /// Without these two there is nothing to map, so their absence fails the whole file rather
    /// than every line. <c>label</c> is optional.
    /// </summary>
    internal static readonly string[] Required = [CrmCode, ExternalCode];

    internal const string Header = $"{CrmCode}, {ExternalCode}, {Label}";
}
