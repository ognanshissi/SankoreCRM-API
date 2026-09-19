namespace Sankore.Modules.Leads.Features.Import;

using Microsoft.Extensions.Configuration;

internal sealed class LocalImportFileStore(IConfiguration config) : IImportFileStore
{
    private string BasePath => config["Leads:ImportFilePath"]
        ?? Path.Combine(Path.GetTempPath(), "sankore-imports");

    public async Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        var dir = BasePath;
        Directory.CreateDirectory(dir);

        var key = $"{Guid.NewGuid():N}{Path.GetExtension(originalFileName)}";
        var fullPath = Path.Combine(dir, key);

        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file, ct);

        return key;
    }

    public Task<Stream> ReadAsync(string fileReference, CancellationToken ct)
    {
        var fullPath = Path.Combine(BasePath, fileReference);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Import file not found: {fileReference}");

        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }

    public Task DeleteAsync(string fileReference, CancellationToken ct)
    {
        var fullPath = Path.Combine(BasePath, fileReference);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }
}
