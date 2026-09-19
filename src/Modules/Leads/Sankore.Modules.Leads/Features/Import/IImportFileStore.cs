namespace Sankore.Modules.Leads.Features.Import;

public interface IImportFileStore
{
    /// <summary>Stores the file and returns an opaque reference (key).</summary>
    Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct);

    /// <summary>Opens the file identified by its opaque reference for reading.</summary>
    Task<Stream> ReadAsync(string fileReference, CancellationToken ct);

    /// <summary>Deletes the file after processing is complete.</summary>
    Task DeleteAsync(string fileReference, CancellationToken ct);
}
