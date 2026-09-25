namespace Sankore.Modules.Leads.Features.LeadSources.Sdk;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Stores and serves SDK JS files. MVP: local filesystem (wwwroot/sdk/).
/// </summary>
internal interface ISdkFileStore
{
    Task WriteAsync(string version, string fileName, byte[] content, CancellationToken ct);
    Task<byte[]?> ReadAsync(string version, string fileName, CancellationToken ct);
    bool Exists(string version, string fileName);

    /// <summary>Version folders currently present in the store (unordered).</summary>
    IEnumerable<string> ListVersions();
}

internal sealed class LocalSdkFileStore(string basePath) : ISdkFileStore
{
    /// <summary>
    /// Resolves the SDK storage root: <c>Leads:SdkStoragePath</c> when configured (point it at a
    /// mounted volume so published versions survive a restart), otherwise <c>sdk/</c> under the
    /// host's web root — where the shipped SDK actually lives. Never AppContext.BaseDirectory:
    /// wwwroot is a static web asset and is not copied to the build output.
    /// </summary>
    public static string ResolveBasePath(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config["Leads:SdkStoragePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        // WebRootPath is null when the wwwroot folder does not exist yet.
        var webRoot = string.IsNullOrEmpty(env.WebRootPath)
            ? Path.Combine(env.ContentRootPath, "wwwroot")
            : env.WebRootPath;

        return Path.Combine(webRoot, "sdk");
    }

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

    public IEnumerable<string> ListVersions()
        => Directory.Exists(basePath)
            ? Directory.EnumerateDirectories(basePath).Select(d => Path.GetFileName(d)!)
            : [];
}
