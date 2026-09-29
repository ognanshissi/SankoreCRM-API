namespace Sankore.Modules.Customers.Features.Import;

/// <summary>
/// Abstraction over import sources. Each implementation reads its source and normalizes rows
/// into <see cref="ImportClientRow"/>, doing no validation of its own.
/// </summary>
public interface IClientImportSourceReader
{
    Task<List<ImportClientRow>> ReadAsync(string sourceReference, CancellationToken ct);
}
