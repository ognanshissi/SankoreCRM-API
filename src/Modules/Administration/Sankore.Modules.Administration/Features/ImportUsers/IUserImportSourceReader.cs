namespace Sankore.Modules.Administration.Features.ImportUsers;

/// <summary>
/// Abstraction over import sources. Each implementation reads raw data
/// from its source and normalizes it into <see cref="ImportUserRow"/> records.
/// </summary>
public interface IUserImportSourceReader
{
    Task<List<ImportUserRow>> ReadAsync(string sourceReference, CancellationToken ct);
}
