namespace Sankore.Shared.Kernel;

/// <summary>
/// File store
/// </summary>
public interface IFileStore
{
    Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct);
    Task<Stream> ReadAsync(string fileReference, CancellationToken ct);
    Task DeleteAsync(string fileReference, CancellationToken ct);
}
