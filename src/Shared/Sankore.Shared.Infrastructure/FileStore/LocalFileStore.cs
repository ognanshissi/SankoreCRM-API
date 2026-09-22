using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.FileStore;

public class LocalFileStore: IFileStore
{
    private static readonly string BasePath = Path.Combine(
        Path.GetTempPath(), "sankore-crm", "storage");

    public async Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        Directory.CreateDirectory(BasePath);
        var ext = Path.GetExtension(originalFileName);
        var key = $"{Guid.NewGuid()}{ext}";
        var path = Path.Combine(BasePath, key);

        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
        return key;
    }

    public Task<Stream> ReadAsync(string fileReference, CancellationToken ct)
    {
        var path = Path.Combine(BasePath, fileReference);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task DeleteAsync(string fileReference, CancellationToken ct)
    {
        var path = Path.Combine(BasePath, fileReference);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}