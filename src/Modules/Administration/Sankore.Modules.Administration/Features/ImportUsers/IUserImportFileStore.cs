namespace Sankore.Modules.Administration.Features.ImportUsers;

/// <summary>
/// File store for user import uploads. Same contract as the Leads module's
/// IImportFileStore but scoped to the Administration module.
/// </summary>
public interface IUserImportFileStore
{
    Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct);
    Task<Stream> ReadAsync(string fileReference, CancellationToken ct);
    Task DeleteAsync(string fileReference, CancellationToken ct);
}
