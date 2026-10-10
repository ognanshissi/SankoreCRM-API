namespace Sankore.Modules.Integration.Features.Mappings.Csv;

/// <summary>
/// The single place a line is judged (INT-04, criterion 2). Pure: no database, no tenant, no
/// clock — so the same verdicts come out of the dry run and out of the real import, which is the
/// only thing that makes the dry run worth running.
/// </summary>
internal sealed class MappingImportValidator
{
    /// <summary>Column widths of <c>integration_mapping</c>; a longer value would be truncated.</summary>
    private const int CodeMaxLength = 100;
    private const int LabelMaxLength = 200;

    internal MappingImportReport Validate(IReadOnlyList<MappingImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // In-file collisions: the database cannot tell us about those. Two lines carrying the
        // same crm_code are an upsert of one another, so importing them would silently keep
        // whichever happened to be last — and nobody would be told which external code won.
        //
        // Ordinal, not case-insensitive: the unique index is on the raw value and the domain
        // trims but never case-folds, so "CI" and "ci" are two legitimately different rows.
        var collided = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.CrmCode))
            .GroupBy(r => r.CrmCode!.Trim(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(r => r.RowNumber).ToArray(), StringComparer.Ordinal);

        var results = new List<MappingRowResult>(rows.Count);

        foreach (var row in rows)
        {
            var errors = new List<string>();

            var crmCode = row.CrmCode?.Trim();
            var externalCode = row.ExternalCode?.Trim();
            var label = row.Label?.Trim();

            if (string.IsNullOrWhiteSpace(crmCode))
                errors.Add($"Column '{MappingCsvColumns.CrmCode}' is empty: there is no CRM code to map.");
            else if (crmCode.Length > CodeMaxLength)
                errors.Add(
                    $"CRM code '{crmCode}' is {crmCode.Length} characters long; "
                    + $"the maximum is {CodeMaxLength}.");

            if (string.IsNullOrWhiteSpace(externalCode))
                errors.Add(
                    $"Column '{MappingCsvColumns.ExternalCode}' is empty: a mapping with no external "
                    + "code would send nothing. Delete the mapping instead of blanking it.");
            else if (externalCode.Length > CodeMaxLength)
                errors.Add(
                    $"External code '{externalCode}' is {externalCode.Length} characters long; "
                    + $"the maximum is {CodeMaxLength}.");

            if (label is { Length: > LabelMaxLength })
                errors.Add($"Label is {label.Length} characters long; the maximum is {LabelMaxLength}.");

            // Every line of a colliding group is refused, not just the later ones: the file does
            // not say which external code the operator meant, and guessing is how the wrong code
            // reaches the CBS.
            if (crmCode is not null && collided.TryGetValue(crmCode, out var lines))
                errors.Add(
                    $"CRM code '{crmCode}' appears on lines {string.Join(", ", lines)} of this file. "
                    + "Keep exactly one line per CRM code.");

            results.Add(new MappingRowResult(
                row.RowNumber, row.CrmCode, row.ExternalCode, errors.Count == 0, errors));
        }

        return MappingImportReport.From(results);
    }
}
