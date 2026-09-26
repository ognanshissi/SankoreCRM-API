namespace Sankore.Modules.Leads.Features.Import;

/// <summary>
/// Abstraction over import sources. Each implementation reads raw data from its
/// source and normalizes it into <see cref="ImportLeadRow"/> records.
/// </summary>
public interface ILeadImportSourceReader
{
    Task<List<ImportLeadRow>> ReadAsync(string sourceReference, CancellationToken ct);
}
