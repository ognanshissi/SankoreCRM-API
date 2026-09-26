using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.FileStore;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Filesystem-backed <see cref="IFileStore"/> for transient uploads (import files).
/// Root comes from "FileStore:BasePath" — point it at a mounted volume when the
/// uploader and the background worker are not the same process.
/// </summary>
public class LocalFileStore(IConfiguration config) : IFileStore
{
    private string BasePath => config["FileStore:BasePath"]
        ?? Path.Combine(Path.GetTempPath(), "sankore-crm", "storage");

    public async Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        var dir = BasePath;
        Directory.CreateDirectory(dir);

        var key  = $"{Guid.NewGuid():N}{Path.GetExtension(originalFileName)}";
        var path = Path.Combine(dir, key);

        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
        return key;
    }

    public Task<Stream> ReadAsync(string fileReference, CancellationToken ct)
    {
        var path = Path.Combine(BasePath, fileReference);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Stored file not found: {fileReference}");

        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task DeleteAsync(string fileReference, CancellationToken ct)
    {
        var path = Path.Combine(BasePath, fileReference);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
