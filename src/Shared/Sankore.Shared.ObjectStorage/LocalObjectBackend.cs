namespace Sankore.Shared.ObjectStorage;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Filesystem backend: one file per object, under a configured root, the key being the relative
/// path. The default everywhere no bucket is configured, and the source side of the migration to
/// one.
///
/// <para>
/// The key maps to the path verbatim, with <c>/</c> as the separator on every platform. That is
/// what makes the move to R2 a copy rather than a conversion: the keys a bucket ends up holding
/// are the paths the volume held, so an object written by one backend is found by the other and
/// a half-finished migration still serves every document.
/// </para>
/// </summary>
public sealed class LocalObjectBackend : IObjectBackend
{
    private readonly string _basePath;
    private readonly ILogger<LocalObjectBackend> _logger;

    public LocalObjectBackend(string basePath, ILogger<LocalObjectBackend> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        // Rooted once, here: every later containment check compares against an absolute path, and
        // comparing against a relative one would pass for any working directory the process is
        // ever restarted in.
        _basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
        _logger = logger;
    }

    /// <summary>Exposed for diagnostics and for the migration, which reports where it read from.</summary>
    public string BasePath => _basePath;

    public async Task PutAsync(string objectKey, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePathOrThrow(objectKey);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write aside then move: a crash mid-write would otherwise leave a truncated object that
        // only surfaces much later — for an encrypted object, as an authentication failure
        // indistinguishable from tampering.
        var temporaryPath = path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, content, ct);

        try
        {
            File.Move(temporaryPath, path, overwrite: false);
        }
        catch
        {
            // A collision means the caller reused a key; leaving the .tmp behind would make the
            // volume grow on every retry of a bug nobody is watching.
            File.Delete(temporaryPath);
            throw;
        }
    }

    public async Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken ct = default)
    {
        if (!TryResolvePath(objectKey, out var path)) return null;

        var info = new FileInfo(path);
        if (!info.Exists) return null;

        if (info.Length > maxBytes)
        {
            _logger.LogWarning(
                "Object {ObjectKey} is {Length} bytes, beyond the {MaxBytes} the caller accepts; refusing to read it",
                objectKey, info.Length, maxBytes);
            return null;
        }

        return await File.ReadAllBytesAsync(path, ct);
    }

    public Task<bool> DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        if (!TryResolvePath(objectKey, out var path) || !File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <summary>
    /// <paramref name="keyPrefix"/> filters keys that already exist; it is never turned into a
    /// path. An enumeration therefore cannot escape the root however the prefix is spelled, and
    /// the prefix means the same thing here as the one S3 matches on.
    /// </summary>
    public async IAsyncEnumerable<string> ListAsync(
        string keyPrefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        keyPrefix ??= string.Empty;

        if (!Directory.Exists(_basePath)) yield break;

        foreach (var path in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            // Interrupted writes, not objects. Returning one would hand the migration a key that
            // no Get can resolve.
            if (path.EndsWith(".tmp", StringComparison.Ordinal)) continue;

            var key = Path.GetRelativePath(_basePath, path).Replace(Path.DirectorySeparatorChar, '/');

            if (key.StartsWith(keyPrefix, StringComparison.Ordinal)) yield return key;
        }

        await Task.CompletedTask;
    }

    private string ResolvePathOrThrow(string objectKey)
    {
        ObjectKey.Validate(objectKey);
        return Contain(objectKey)
            ?? throw new ArgumentException(
                $"'{objectKey}' resolves outside the object root.", nameof(objectKey));
    }

    /// <summary>
    /// Reads are told "nothing here" rather than thrown at: a key reaches them from a URL, and an
    /// exception would answer the prober's real question — whether the key was well formed.
    /// </summary>
    private bool TryResolvePath(string objectKey, out string path)
    {
        path = string.Empty;
        if (!ObjectKey.IsValid(objectKey)) return false;

        var resolved = Contain(objectKey);
        if (resolved is null) return false;

        path = resolved;
        return true;
    }

    /// <summary>
    /// The containment check, kept even though <see cref="ObjectKey"/> already refuses every
    /// traversal segment. Both, on purpose: the rule above is a string test that a later edit
    /// could loosen, this one is the filesystem's own answer after symlinks and <c>..</c> have
    /// been resolved, and it is the one that actually bounds the damage.
    /// </summary>
    private string? Contain(string objectKey)
    {
        var candidate = Path.GetFullPath(
            Path.Combine(_basePath, objectKey.Replace('/', Path.DirectorySeparatorChar)));

        return candidate.StartsWith(_basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : null;
    }
}
