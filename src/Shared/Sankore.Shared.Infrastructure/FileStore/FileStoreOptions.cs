namespace Sankore.Shared.Infrastructure.FileStore;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Settings of the import/export file store, bound from the <c>FileStore</c> configuration
/// section (<see cref="SectionName"/>).
///
/// <para>
/// Host/deployment settings, never tenant parameters: a tenant must not be able to raise its own
/// ceiling or move the store's root.
/// </para>
/// </summary>
public sealed class FileStoreOptions
{
    public const string SectionName = "FileStore";

    /// <summary>
    /// Root of the object store. Point it at a mounted volume whenever the uploader and the
    /// background worker are not the same process — and in any real environment, because the
    /// default lives in the container's temp folder and a redeploy destroys it mid-import.
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Largest object this store reads or writes, 64 MiB by default.
    ///
    /// <para>
    /// A number rather than <c>long.MaxValue</c> because <see cref="ObjectStorage.IObjectBackend.GetAsync"/>
    /// takes a ceiling and passing the maximum would be choosing "unbounded" silently — the whole
    /// file lands in a <c>byte[]</c>, so "unbounded" means one request can allocate whatever the
    /// volume happens to hold.
    /// </para>
    ///
    /// <para>
    /// 64 MiB is above everything this store legitimately holds and well below the point where a
    /// single read hurts: uploads are refused past 5 MB at the import endpoints, and the largest
    /// write is a client export, itself capped at 50 000 rows (≈30 MB of CSV at the widest row).
    /// It is enforced on the WRITE side too, deliberately — a ceiling applied only on read would
    /// accept an object and then report it as missing, which reaches the user as
    /// "export not found" long after the job said it had succeeded.
    /// </para>
    /// </summary>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// The object root for the host: <c>FileStore:BasePath</c> when configured, otherwise the
    /// same temp folder the filesystem-only implementation used, so an upgrade finds the files
    /// an in-flight import already wrote.
    /// </summary>
    public string ResolveBasePath() => Resolve(BasePath);

    /// <summary>
    /// The same root from raw configuration, for a host that must declare the backend before the
    /// options are bound — which it must, because the module registrations install a filesystem
    /// fallback and the host's choice has to be in place first.
    /// </summary>
    public static string ResolveBasePath(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Resolve(config[$"{SectionName}:BasePath"]);
    }

    private static string Resolve(string? basePath)
        => string.IsNullOrWhiteSpace(basePath)
            ? Path.Combine(Path.GetTempPath(), "sankore-crm", "storage")
            : Path.GetFullPath(basePath);
}
