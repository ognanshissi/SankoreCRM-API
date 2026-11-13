namespace Sankore.Modules.Leads.Features.LeadSources.Sdk;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// Stores and serves SDK JS files, through the object storage (concern <c>sdk-files</c>, since
/// 2026-11-13): a bucket when one is configured, otherwise the folder below — which is the very
/// layout the files always had (<c>&lt;version&gt;/forms.min.js</c>), so a host without a bucket
/// behaves as before.
/// </summary>
internal interface ISdkFileStore
{
    Task WriteAsync(string version, string fileName, byte[] content, CancellationToken ct);
    Task<byte[]?> ReadAsync(string version, string fileName, CancellationToken ct);

    /// <summary>Version folders currently present in the store (unordered, distinct).</summary>
    Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct);
}

public static class SdkFileStorage
{
    public const string Concern = "sdk-files";

    /// <summary>
    /// The local root when no bucket is configured: <c>Leads:SdkStoragePath</c> when set (a mounted
    /// volume so published versions survive a restart), otherwise <see cref="ShippedRoot"/>. Never
    /// AppContext.BaseDirectory: wwwroot is a static web asset and is not copied to the build output.
    /// </summary>
    public static string ResolveBasePath(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Leads:SdkStoragePath"];
        return string.IsNullOrWhiteSpace(configured) ? ShippedRoot(env) : Path.GetFullPath(configured);
    }

    /// <summary>Where the builds shipped inside the image live: <c>sdk/</c> under the web root.</summary>
    public static string ShippedRoot(IHostEnvironment env)
    {
        // WebRootPath is null when the wwwroot folder does not exist yet.
        var webRoot = env is IWebHostEnvironment web && !string.IsNullOrEmpty(web.WebRootPath)
            ? web.WebRootPath
            : Path.Combine(env.ContentRootPath, "wwwroot");

        return Path.Combine(webRoot, "sdk");
    }
}

internal sealed class ObjectSdkFileStore(IObjectBackend backend) : ISdkFileStore
{
    /// <summary>A form script, not an asset library: bounds what one read may pull into memory.</summary>
    private const long MaxBytes = 5L * 1024 * 1024;

    public Task WriteAsync(string version, string fileName, byte[] content, CancellationToken ct) =>
        backend.PutAsync(KeyOf(version, fileName) ?? throw new ArgumentException($"'{version}/{fileName}' is not a valid SDK file name."), content, ct);

    /// <summary>A version taken from the URL that cannot be a key ("..", a colon) is simply not found.</summary>
    public async Task<byte[]?> ReadAsync(string version, string fileName, CancellationToken ct) =>
        KeyOf(version, fileName) is { } key ? await backend.GetAsync(key, MaxBytes, ct) : null;

    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in backend.ListAsync(string.Empty, ct))
        {
            var slash = key.IndexOf('/');
            if (slash > 0) versions.Add(key[..slash]);
        }
        return [.. versions];
    }

    private static string? KeyOf(string version, string fileName)
    {
        var key = $"{version}/{fileName}";
        return ObjectKey.IsValid(key) && !version.Contains('/') && !fileName.Contains('/') ? key : null;
    }
}

/// <summary>
/// Copies the builds shipped inside the image (<see cref="SdkFileStorage.ShippedRoot"/>) into the
/// store when they are missing there, before <see cref="SdkVersionSeeder"/> registers them. Without
/// a bucket the store IS that folder and nothing is copied. A file already in the store is never
/// overwritten: an exact version is immutable, its SRI hash is pasted on customers' sites.
/// </summary>
internal static class ShippedSdkFiles
{
    public static async Task CopyMissingAsync(string shippedRoot, ISdkFileStore store, ILogger logger, CancellationToken ct = default)
    {
        if (!Directory.Exists(shippedRoot)) return;

        foreach (var dir in Directory.EnumerateDirectories(shippedRoot))
        {
            var version = Path.GetFileName(dir);
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var fileName = Path.GetFileName(path);
                if (await store.ReadAsync(version, fileName, ct) is not null) continue;

                await store.WriteAsync(version, fileName, await File.ReadAllBytesAsync(path, ct), ct);
                logger.LogInformation("Copied shipped SDK file {Version}/{FileName} to the SDK store.", version, fileName);
            }
        }
    }
}
