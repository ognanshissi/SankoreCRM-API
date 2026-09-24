namespace Sankore.Modules.Leads.Features.LeadSources.Sdk;

/// <summary>
/// Stores and serves SDK JS files. MVP: local filesystem (wwwroot/sdk/).
/// </summary>
internal interface ISdkFileStore
{
    Task WriteAsync(string version, string fileName, byte[] content, CancellationToken ct);
    Task<byte[]?> ReadAsync(string version, string fileName, CancellationToken ct);
    bool Exists(string version, string fileName);
}

internal sealed class LocalSdkFileStore(string basePath) : ISdkFileStore
{
    public async Task WriteAsync(string version, string fileName, byte[] content, CancellationToken ct)
    {
        var dir = Path.Combine(basePath, version);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), content, ct);
    }

    public async Task<byte[]?> ReadAsync(string version, string fileName, CancellationToken ct)
    {
        var path = Path.Combine(basePath, version, fileName);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct);
    }

    public bool Exists(string version, string fileName)
        => File.Exists(Path.Combine(basePath, version, fileName));
}
