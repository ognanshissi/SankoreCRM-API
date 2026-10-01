namespace Sankore.Shared.Infrastructure.FileStore;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// <see cref="IFileStore"/> over any <see cref="IObjectBackend"/>: the import spreadsheets the
/// three importers upload and the client exports M01 generates.
///
/// <para>
/// Renamed from <c>LocalFileStore</c> because it no longer is one — the filesystem is now just
/// the backend that happens to be injected, and a deployment moving these files to a bucket
/// changes the registration and nothing here. What stays above the medium is the same list the
/// KYC document store keeps above it: the shape of a reference, the size ceiling, and the
/// read-side semantics callers depend on.
/// </para>
///
/// <para>
/// <b>No tenant partitioning.</b> A reference is a bare GUID with no tenant component, so any
/// tenant's reference resolves in any tenant's context — and client PII exports live in this
/// store. Adding a tenant prefix would invalidate every reference already persisted on a
/// <c>ClientExportJob</c> row and every reference an in-flight Hangfire import job is holding,
/// so it is a deliberate owner decision and not this class's to take unilaterally.
/// </para>
/// </summary>
public sealed class ObjectBackedFileStore : IFileStore
{
    /// <summary>
    /// The exact shape <see cref="StoreAsync"/> issues: 32 lowercase hex characters (a GUID in
    /// <c>N</c> format) and an optional short alphanumeric extension. Anything else is refused
    /// before an object key is built.
    ///
    /// <para>
    /// This is how path traversal is handled — not by sanitising a caller-supplied name, but by
    /// never accepting one. References arrive from request payloads and from Hangfire job
    /// arguments, and the previous implementation handed them straight to
    /// <see cref="Path.Combine(string, string)"/>, where <c>"../../etc/passwd"</c> is a path and
    /// <c>"/etc/passwd"</c> discards the root entirely.
    /// </para>
    ///
    /// <para>
    /// The extension is constrained with the rest because it comes from the uploaded file name,
    /// which is attacker-controlled: it is normalised to lowercase at write time and matched
    /// case-insensitively here, so a reference issued before that normalisation existed still
    /// resolves. <c>\z</c> rather than <c>$</c>: <c>$</c> also matches before a trailing newline,
    /// and <c>"{guid}.csv\n/../../etc/passwd"</c> must not slip through on the next edit.
    /// </para>
    /// </summary>
    private static readonly Regex FileReferenceShape = new(
        @"^[0-9a-f]{32}(\.[a-zA-Z0-9]{1,8})?\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The normalised extension a reference may carry. Checked on the issued name rather than on
    /// the uploaded one: <c>Path.GetExtension</c> of <c>"facture.p hp"</c> or of a name ending in
    /// a right-to-left override is whatever the user typed, and only what matches this reaches a
    /// reference — anything else is dropped and the object is stored without an extension.
    /// </summary>
    private static readonly Regex ExtensionShape = new(
        @"^\.[a-z0-9]{1,8}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IObjectBackend _backend;
    private readonly FileStoreOptions _options;
    private readonly ILogger<ObjectBackedFileStore> _logger;

    public ObjectBackedFileStore(
        IObjectBackend backend,
        IOptions<FileStoreOptions> options,
        ILogger<ObjectBackedFileStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _backend = backend;
        _options = options.Value;
        _logger = logger;

        if (_options.MaxBytes <= 0)
            throw new InvalidOperationException(
                $"{FileStoreOptions.SectionName}:MaxBytes must be a positive number of bytes, "
                + $"but {_options.MaxBytes} was configured.");
    }

    public async Task<string> StoreAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var bytes = await ReadBoundedAsync(content, ct);

        // The reference is random and carries nothing of the uploaded name: the name reaches the
        // user elsewhere (the import job keeps it), and a reference that echoed it would be a
        // caller-supplied string again — the exact thing this class refuses to trust.
        var fileReference = $"{Guid.NewGuid():N}{NormalizeExtension(originalFileName)}";

        await _backend.PutAsync(fileReference, bytes, ct);

        return fileReference;
    }

    /// <summary>
    /// The bytes behind a reference.
    ///
    /// <para>
    /// Still a <see cref="FileNotFoundException"/> when there is nothing to read, because callers
    /// are written against that (<c>DownloadClientExportHandler</c> catches
    /// <see cref="IOException"/> and answers EXPORT_NOT_FOUND). A reference this store did not
    /// issue takes the SAME path, with the same exception and the same message: a distinct answer
    /// — or an <see cref="ArgumentException"/> escaping as a 500 — would tell a prober whether
    /// their guess was well formed, which is the only thing they still need to learn.
    /// </para>
    /// </summary>
    public async Task<Stream> ReadAsync(string fileReference, CancellationToken ct)
    {
        var bytes = IsIssuedByThisStore(fileReference)
            ? await _backend.GetAsync(fileReference, _options.MaxBytes, ct)
            : null;

        if (bytes is null) throw new FileNotFoundException($"Stored file not found: {fileReference}");

        return new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
    }

    /// <summary>
    /// Silently idempotent, as before: deleting an import file twice is normal when a job is
    /// retried, and a rejected reference is simply nothing to delete.
    /// </summary>
    public async Task DeleteAsync(string fileReference, CancellationToken ct)
    {
        if (!IsIssuedByThisStore(fileReference)) return;

        await _backend.DeleteAsync(fileReference, ct);
    }

    /// <summary>
    /// <c>true</c> only for a reference of the exact shape <see cref="StoreAsync"/> issues.
    /// Rejections are logged at Debug: a stale reference after a retention sweep is ordinary
    /// traffic, and a genuine probe shows up as a burst of 404s at the endpoint layer.
    /// </summary>
    private bool IsIssuedByThisStore(string? fileReference)
    {
        if (!string.IsNullOrEmpty(fileReference) && FileReferenceShape.IsMatch(fileReference))
            return true;

        // The reference itself is not logged: it is caller-supplied text heading for a log file.
        _logger.LogDebug("Rejected a file reference that this store did not issue");
        return false;
    }

    /// <summary>
    /// Reads at most <see cref="FileStoreOptions.MaxBytes"/> and gives up the moment the stream
    /// goes past it, so an endless upload never becomes an endless allocation. A seekable stream
    /// is measured first and refused without a byte read.
    /// </summary>
    private async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        var max = _options.MaxBytes;

        if (content.CanSeek && content.Length - content.Position > max)
            throw TooLarge(content.Length - content.Position, max);

        using var buffer = new MemoryStream(capacity: 64 * 1024);
        var chunk = new byte[64 * 1024];
        long total = 0;

        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > max) throw TooLarge(total, max);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The extension to append to a reference, or an empty string. Lowercased so the store holds
    /// one spelling per type, and dropped entirely unless it is short and alphanumeric — the
    /// importers only ever ask whether a reference ends in <c>.xlsx</c>, so nothing of value is
    /// lost when an exotic name is filed without one.
    /// </summary>
    private static string NormalizeExtension(string? originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName)) return string.Empty;

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

        return ExtensionShape.IsMatch(extension) ? extension : string.Empty;
    }

    private static DomainException TooLarge(long size, long max)
        => new($"FILE_TOO_LARGE: the file is {size} bytes, the limit is {max} bytes.");
}
