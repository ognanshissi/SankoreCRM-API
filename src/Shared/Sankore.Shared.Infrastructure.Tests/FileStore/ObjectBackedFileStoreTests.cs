namespace Sankore.Shared.Infrastructure.Tests.FileStore;

using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.FileStore;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// The store runs against the REAL filesystem backend here, not the in-memory one: what is being
/// pinned is that a reference cannot reach a file, and an in-memory dictionary has no files to
/// reach. The temporary root has a sibling directory and a secret above it, so a reference that
/// escapes has somewhere to escape to — a test where traversal leads nowhere proves nothing.
/// </summary>
public sealed class ObjectBackedFileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _secretAboveTheRoot;
    private readonly LocalObjectBackend _backend;

    public ObjectBackedFileStoreTests()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"sankore-filestore-{Guid.NewGuid():N}");
        _root = Path.Combine(sandbox, "storage");
        Directory.CreateDirectory(_root);

        _secretAboveTheRoot = Path.Combine(sandbox, "secret.txt");
        File.WriteAllText(_secretAboveTheRoot, "credentials");

        _backend = new LocalObjectBackend(_root, NullLogger<LocalObjectBackend>.Instance);
    }

    public void Dispose()
    {
        var sandbox = Directory.GetParent(_root)!.FullName;
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
    }

    private IFileStore Sut(long maxBytes = 64L * 1024 * 1024)
        => new ObjectBackedFileStore(
            _backend,
            Options.Create(new FileStoreOptions { BasePath = _root, MaxBytes = maxBytes }),
            NullLogger<ObjectBackedFileStore>.Instance);

    private static Stream Bytes(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static async Task<string> TextOf(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task A_stored_file_comes_back_byte_for_byte()
    {
        var sut = Sut();

        var reference = await sut.StoreAsync(Bytes("nom;prénom\nKOUASSI;Aya"), "clients.csv", CancellationToken.None);

        (await TextOf(await sut.ReadAsync(reference, CancellationToken.None)))
            .Should().Be("nom;prénom\nKOUASSI;Aya");
    }

    [Fact]
    public async Task A_reference_is_a_random_identifier_and_never_the_uploaded_name()
    {
        var sut = Sut();

        var reference = await sut.StoreAsync(Bytes("x"), "../../etc/Clients Export FINAL.csv", CancellationToken.None);

        reference.Should().MatchRegex(@"^[0-9a-f]{32}\.csv\z");
        reference.Should().NotContain("Clients");
    }

    /// <summary>
    /// The importers decide CSV vs XLSX on the reference's own suffix
    /// (<c>sourceReference.EndsWith(".xlsx")</c>), so normalising the extension is not cosmetic:
    /// drop the wrong one and a spreadsheet is parsed as CSV.
    /// </summary>
    [Theory]
    [InlineData("LEADS-2026.XLSX", ".xlsx")]
    [InlineData("clients.csv", ".csv")]
    [InlineData("clients", "")]
    [InlineData("payload.p hp", "")]
    [InlineData("payload.unreasonablylongextension", "")]
    [InlineData("payload.", "")]
    public async Task An_extension_is_lowercased_when_it_is_plausible_and_dropped_otherwise(
        string uploadedName, string expectedExtension)
    {
        var sut = Sut();

        var reference = await sut.StoreAsync(Bytes("x"), uploadedName, CancellationToken.None);

        reference.Should().Be(reference[..32] + expectedExtension);
    }

    /// <summary>
    /// The defect this class was rewritten for: <c>Path.Combine(root, "../../secret.txt")</c> is a
    /// path above the root, and <c>Path.Combine(root, "/etc/passwd")</c> discards the root
    /// entirely.
    /// </summary>
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\secret.txt")]
    public async Task A_reference_that_leaves_the_root_reads_as_an_absent_file(string reference)
    {
        var sut = Sut();

        var read = () => sut.ReadAsync(reference, CancellationToken.None);

        await read.Should().ThrowAsync<FileNotFoundException>();
        File.ReadAllText(_secretAboveTheRoot).Should().Be("credentials");
    }

    /// <summary>
    /// The case the backend alone does NOT catch, and therefore the one that measures this
    /// store's own guard: <c>kyc-documents/ab/…</c> is a perfectly legal object key, so without
    /// the reference-shape check a caller reads any object the root holds — including another
    /// concern's, when a deployment points several at the same volume.
    /// </summary>
    [Fact]
    public async Task A_reference_naming_another_object_in_the_same_root_is_refused()
    {
        await _backend.PutAsync("kyc-documents/ab/abcdef.kycobj", Encoding.UTF8.GetBytes("evidence"));
        var sut = Sut();

        var read = () => sut.ReadAsync("kyc-documents/ab/abcdef.kycobj", CancellationToken.None);

        await read.Should().ThrowAsync<FileNotFoundException>();
        (await _backend.GetAsync("kyc-documents/ab/abcdef.kycobj", long.MaxValue))
            .Should().NotBeNull("the object must still be there — a read must not be a delete either");
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("kyc-documents/ab/abcdef.kycobj")]
    public async Task Delete_removes_nothing_it_did_not_issue(string reference)
    {
        await _backend.PutAsync("kyc-documents/ab/abcdef.kycobj", Encoding.UTF8.GetBytes("evidence"));
        var sut = Sut();

        // Silently idempotent, as before: a retried import job deletes its file twice.
        await sut.DeleteAsync(reference, CancellationToken.None);

        File.ReadAllText(_secretAboveTheRoot).Should().Be("credentials");
        (await _backend.GetAsync("kyc-documents/ab/abcdef.kycobj", long.MaxValue)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_removes_what_it_did_issue_and_stays_idempotent()
    {
        var sut = Sut();
        var reference = await sut.StoreAsync(Bytes("x"), "clients.csv", CancellationToken.None);

        await sut.DeleteAsync(reference, CancellationToken.None);
        await sut.DeleteAsync(reference, CancellationToken.None);

        await FluentActions.Awaiting(() => sut.ReadAsync(reference, CancellationToken.None))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    /// <summary>
    /// A malformed reference and an unknown-but-well-formed one must be answered identically.
    /// A different exception — or a message saying the reference was rejected — tells a prober
    /// the one thing they cannot otherwise learn: whether their guess had the right shape.
    /// </summary>
    [Fact]
    public async Task A_rejected_reference_is_indistinguishable_from_an_unknown_one()
    {
        var sut = Sut();

        var malformed = (await FluentActions
            .Awaiting(() => sut.ReadAsync("../secret.txt", CancellationToken.None))
            .Should().ThrowAsync<FileNotFoundException>()).Which;

        var unknown = (await FluentActions
            .Awaiting(() => sut.ReadAsync($"{Guid.NewGuid():N}.csv", CancellationToken.None))
            .Should().ThrowAsync<FileNotFoundException>()).Which;

        malformed.GetType().Should().Be(unknown.GetType());
        Regex.Replace(malformed.Message, @"[^\s:]+\z", "REF")
            .Should().Be(Regex.Replace(unknown.Message, @"[^\s:]+\z", "REF"));
    }

    [Fact]
    public async Task A_file_beyond_the_ceiling_is_refused_on_write_rather_than_stored_unreadable()
    {
        var sut = Sut(maxBytes: 1024);

        var store = () => sut.StoreAsync(new MemoryStream(new byte[2048]), "big.csv", CancellationToken.None);

        (await store.Should().ThrowAsync<DomainException>()).Which.Message.Should().Contain("FILE_TOO_LARGE");
        Directory.EnumerateFiles(_root).Should().BeEmpty("nothing may be left behind");
    }

    /// <summary>
    /// A non-seekable stream cannot be measured up front, so the ceiling has to bite while the
    /// bytes are being read — otherwise an endless upload is an endless allocation.
    /// </summary>
    [Fact]
    public async Task A_stream_that_cannot_be_measured_is_still_cut_off_at_the_ceiling()
    {
        var sut = Sut(maxBytes: 1024);

        var store = () => sut.StoreAsync(
            new UnmeasurableStream(new byte[4096]), "big.csv", CancellationToken.None);

        await store.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task An_object_beyond_the_ceiling_reads_as_absent()
    {
        var reference = $"{Guid.NewGuid():N}.csv";
        await _backend.PutAsync(reference, new byte[4096]);
        var sut = Sut(maxBytes: 1024);

        await FluentActions.Awaiting(() => sut.ReadAsync(reference, CancellationToken.None))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public void A_ceiling_of_zero_fails_at_construction_rather_than_on_the_first_upload()
    {
        var build = () => new ObjectBackedFileStore(
            _backend,
            Options.Create(new FileStoreOptions { MaxBytes = 0 }),
            NullLogger<ObjectBackedFileStore>.Instance);

        build.Should().Throw<InvalidOperationException>().WithMessage("*MaxBytes*");
    }

    /// <summary>Readable, not seekable — an <c>IFormFile</c> body on the wire.</summary>
    private sealed class UnmeasurableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
