namespace Sankore.Modules.Kyc.Tests.Infrastructure.Storage;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// These tests are the compliance argument for the KYC object store: an ID-card scan is
/// unreadable on the volume, a reference issued to one tenant is useless to another, a crafted
/// reference cannot reach outside the store, and the digest proves the image behind a decision
/// was never swapped. Each one pins a property a reviewer would otherwise have to re-derive
/// from the implementation.
/// </summary>
public sealed class KycDocumentStoreTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid KycFileId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sankore-kyc-tests", Guid.NewGuid().ToString("N"));

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly string _key = NewKey();

    /// <summary>
    /// Over the REAL filesystem backend, not a substitute. These tests are the compliance
    /// argument, and most of them assert on what is actually on the volume — an in-memory
    /// backend would prove the store encrypts something, not that an ID scan is unreadable
    /// where it is kept.
    /// </summary>
    private KycDocumentStore CreateStore(long? maxBytes = null, string? key = null) =>
        new(Backend(),
            Options.Create(new KycStorageOptions
            {
                BasePath = _root,
                EncryptionKey = key ?? _key,
                MaxBytes = maxBytes ?? 10L * 1024 * 1024
            }),
            NullLogger<KycDocumentStore>.Instance);

    private LocalObjectBackend Backend() =>
        new(_root, NullLogger<LocalObjectBackend>.Instance);

    private static MemoryStream Jpeg(int size = 2048)
    {
        var bytes = new byte[size];
        // Deterministic but not uniform: a run of zeroes would also "not appear" in ciphertext.
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i * 31 % 251 + 1);
        return new MemoryStream(bytes);
    }

    private static async Task<byte[]> DrainAsync(Stream? stream)
    {
        stream.Should().NotBeNull();
        await using var owned = stream!;
        using var buffer = new MemoryStream();
        await owned.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private string[] ObjectFiles() =>
        Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*.kycobj", SearchOption.AllDirectories)
            : [];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---------------------------------------------------------------- round trip

    [Theory]
    [InlineData(KycDocumentKind.IdentityDocumentFront, "image/jpeg")]
    [InlineData(KycDocumentKind.IdentityDocumentBack, "image/png")]
    [InlineData(KycDocumentKind.Selfie, "application/pdf")]
    public async Task Store_then_open_should_return_byte_identical_content(
        KycDocumentKind kind, string contentType)
    {
        var sut = CreateStore();
        using var source = Jpeg();
        var expected = source.ToArray();

        var stored = await sut.StoreAsync(TenantA, KycFileId, kind, source, contentType, default);
        var reopened = await DrainAsync(await sut.OpenAsync(TenantA, stored.StorageRef, default));

        reopened.Should().Equal(expected);
        stored.ContentType.Should().Be(contentType);
        stored.SizeBytes.Should().Be(expected.Length);
    }

    [Fact]
    public async Task A_content_type_with_parameters_should_be_normalized_not_rejected()
    {
        var sut = CreateStore();
        using var source = Jpeg();

        var stored = await sut.StoreAsync(
            TenantA, KycFileId, KycDocumentKind.Selfie, source, "IMAGE/JPEG; charset=binary", default);

        stored.ContentType.Should().Be("image/jpeg");
    }

    // ---------------------------------------------------------------- encrypted at rest

    [Fact]
    public async Task The_object_on_disk_should_not_contain_the_plaintext()
    {
        var sut = CreateStore();
        var secret = Encoding.UTF8.GetBytes("CARTE NATIONALE D'IDENTITE — CI0123456789");
        using var source = new MemoryStream(secret);

        await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.IdentityDocumentFront, source, "image/jpeg", default);

        var onDisk = await File.ReadAllBytesAsync(ObjectFiles().Single());
        Encoding.UTF8.GetString(onDisk).Should().NotContain("CI0123456789");
        onDisk.Length.Should().Be(secret.Length + 32); // "KYC1" + nonce(12) + tag(16)
        onDisk.Skip(32).Should().NotEqual(secret, "the payload must be ciphertext, not a copy");
    }

    [Fact]
    public async Task The_same_image_stored_twice_should_produce_different_ciphertext()
    {
        var sut = CreateStore();
        using var first = Jpeg();
        using var second = new MemoryStream(first.ToArray());

        var a = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, first, "image/jpeg", default);
        var b = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, second, "image/jpeg", default);

        a.StorageRef.Should().NotBe(b.StorageRef);
        a.Sha256.Should().Be(b.Sha256, "the digest is of the plaintext, which is identical");

        // A reused nonce would void AES-GCM entirely; two objects must never match byte for byte.
        var files = ObjectFiles().OrderBy(f => f).ToArray();
        var left = await File.ReadAllBytesAsync(files[0]);
        var right = await File.ReadAllBytesAsync(files[1]);
        left.Should().NotEqual(right);
    }

    [Fact]
    public async Task A_tampered_object_should_fail_loudly_rather_than_return_bytes()
    {
        var sut = CreateStore();
        using var source = Jpeg();
        var stored = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        var path = ObjectFiles().Single();
        var raw = await File.ReadAllBytesAsync(path);
        raw[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, raw);

        var act = () => sut.OpenAsync(TenantA, stored.StorageRef, default);

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain("KYC_DOCUMENT_INTEGRITY_FAILURE");
    }

    [Fact]
    public async Task An_object_file_moved_into_another_tenant_folder_should_not_decrypt()
    {
        var sut = CreateStore();
        using var source = Jpeg();
        var storedForA = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        // Same object id, re-filed under tenant B — the attack the GCM associated data exists for.
        using var decoy = Jpeg(64);
        var storedForB = await sut.StoreAsync(TenantB, KycFileId, KycDocumentKind.Selfie, decoy, "image/jpeg", default);
        var pathA = ObjectFileOf(storedForA);
        var pathB = ObjectFileOf(storedForB);
        File.Copy(pathA, pathB, overwrite: true);

        var act = () => sut.OpenAsync(TenantB, storedForB.StorageRef, default);

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain("KYC_DOCUMENT_INTEGRITY_FAILURE");
    }

    // ---------------------------------------------------------------- tenant scoping and traversal

    [Fact]
    public async Task A_reference_issued_to_one_tenant_should_not_be_readable_by_another()
    {
        var sut = CreateStore();
        using var source = Jpeg();
        var stored = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        var crossTenant = await sut.OpenAsync(TenantB, stored.StorageRef, default);

        crossTenant.Should().BeNull("existence of KYC evidence must not leak across tenants");
        (await sut.DeleteAsync(TenantB, stored.StorageRef, default)).Should().BeFalse();
        (await DrainAsync(await sut.OpenAsync(TenantA, stored.StorageRef, default))).Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_reference_should_carry_no_tenant_or_file_identifier()
    {
        var sut = CreateStore();
        using var source = Jpeg();

        var stored = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        stored.StorageRef.Should().NotContain(TenantA.ToString("N"));
        stored.StorageRef.Should().NotContain(KycFileId.ToString("N"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("k1.0011223344556677.../../../etc/passwd")]
    [InlineData("k1.0011223344556677.0011223344556677001122334455667")]       // one hex short
    [InlineData("k1.0011223344556677.00112233445566770011223344556677\n/../x")] // newline smuggling
    [InlineData("K1.0011223344556677.00112233445566770011223344556677")]      // wrong prefix case
    [InlineData("k1.0011223344556677.00112233445566770011223344556ZZZ")]      // non-hex
    public async Task A_malformed_reference_should_be_refused_without_throwing(string storageRef)
    {
        var sut = CreateStore();

        (await sut.OpenAsync(TenantA, storageRef, default)).Should().BeNull();
        (await sut.DeleteAsync(TenantA, storageRef, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_well_formed_but_unknown_reference_should_simply_be_absent()
    {
        var sut = CreateStore();
        using var source = Jpeg();
        var stored = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        // Same tenant segment, different object id: reaches the filesystem and finds nothing.
        var unknown = stored.StorageRef[..^4] + "dead";

        (await sut.OpenAsync(TenantA, unknown, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_crafted_reference_should_not_reach_a_file_outside_the_store()
    {
        var sut = CreateStore();
        using var source = Jpeg();
        var stored = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);
        var tenantSegment = stored.StorageRef.Split('.')[1];

        var outside = Path.Combine(Path.GetTempPath(), $"sankore-kyc-outside-{Guid.NewGuid():N}.kycobj");
        await File.WriteAllTextAsync(outside, "not yours");
        try
        {
            // Tenant segment genuinely belongs to TenantA, so these get past the cheap check and
            // are stopped by the shape alone — the reason no caller-supplied name is ever joined.
            string[] escapes =
            [
                $"k1.{tenantSegment}.../../../../../../../../etc/passwd",
                $"k1.{tenantSegment}.{Path.GetFileNameWithoutExtension(outside)}",
                $"k1.{tenantSegment}.{new string('.', 32)}",
                $"k1.{tenantSegment}.{Path.GetFullPath(outside)}"
            ];

            foreach (var escape in escapes)
            {
                (await sut.OpenAsync(TenantA, escape, default)).Should().BeNull();
                (await sut.DeleteAsync(TenantA, escape, default)).Should().BeFalse();
            }

            File.Exists(outside).Should().BeTrue("delete must not reach outside either");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ---------------------------------------------------------------- limits

    [Theory]
    [InlineData("image/gif")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    public async Task A_content_type_outside_the_allow_list_should_be_rejected(string contentType)
    {
        var sut = CreateStore();
        using var source = Jpeg();

        var act = () => sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, contentType, default);

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain("KYC_DOCUMENT_CONTENT_TYPE_NOT_ALLOWED");
        ObjectFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Content_over_the_limit_should_be_rejected_before_it_is_fully_read()
    {
        var sut = CreateStore(maxBytes: 1024);
        using var source = new CountingStream(new byte[512 * 1024], seekable: false);

        var act = () => sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain("KYC_DOCUMENT_TOO_LARGE");
        source.BytesRead.Should().BeLessThan(512 * 1024,
            "the store must give up on an oversized upload, not buffer it to measure it");
        ObjectFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversized_seekable_stream_should_be_rejected_without_reading_a_byte()
    {
        var sut = CreateStore(maxBytes: 1024);
        using var source = new CountingStream(new byte[4096], seekable: true);

        var act = () => sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        await act.Should().ThrowAsync<DomainException>();
        source.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task Content_exactly_at_the_limit_should_be_accepted()
    {
        var sut = CreateStore(maxBytes: 1024);
        using var source = Jpeg(1024);

        var stored = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/png", default);

        stored.SizeBytes.Should().Be(1024);
    }

    [Fact]
    public async Task An_empty_upload_should_be_rejected()
    {
        var sut = CreateStore();
        using var source = new MemoryStream();

        var act = () => sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain("KYC_DOCUMENT_EMPTY");
    }

    // ---------------------------------------------------------------- digest

    [Fact]
    public async Task The_returned_digest_should_be_the_sha256_of_the_plaintext()
    {
        var sut = CreateStore();
        using var source = Jpeg();
        var expected = Convert.ToHexString(SHA256.HashData(source.ToArray())).ToLowerInvariant();

        var stored = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        stored.Sha256.Should().Be(expected);

        // The point of the digest: re-reading the object reproduces it, so a swap is detectable.
        var reopened = await DrainAsync(await sut.OpenAsync(TenantA, stored.StorageRef, default));
        Convert.ToHexString(SHA256.HashData(reopened)).ToLowerInvariant().Should().Be(expected);
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public async Task Delete_should_report_what_it_actually_removed()
    {
        var sut = CreateStore();
        using var source = Jpeg();
        var stored = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, source, "image/jpeg", default);

        (await sut.DeleteAsync(TenantA, stored.StorageRef, default)).Should().BeTrue();
        (await sut.OpenAsync(TenantA, stored.StorageRef, default)).Should().BeNull();

        // Idempotent: a retention sweep replayed after a crash must not blow up.
        (await sut.DeleteAsync(TenantA, stored.StorageRef, default)).Should().BeFalse();
        ObjectFiles().Should().BeEmpty();
    }

    // ---------------------------------------------------------------- configuration

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!!")]
    [InlineData("c2hvcnQ=")] // valid Base64, 5 bytes — not an AES-256 key
    public void A_missing_or_invalid_encryption_key_should_fail_at_construction(string? configured)
    {
        var act = () => new KycDocumentStore(
            Backend(),
            Options.Create(new KycStorageOptions { BasePath = _root, EncryptionKey = configured }),
            NullLogger<KycDocumentStore>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Kyc:Storage:EncryptionKey",
                "the message must name the setting to fix, not surface as ArgumentNullException "
                + "(Parameter 's') from Convert.FromBase64String halfway through an upload");
    }

    [Fact]
    public void The_base_path_should_come_from_configuration_when_set()
    {
        var configured = Path.Combine(_root, "mounted-volume");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kyc:Storage:BasePath"] = configured })
            .Build();

        KycStorageOptions.ResolveBasePath(config, HostEnvironment("/srv/app"))
            .Should().Be(Path.GetFullPath(configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_base_path_should_fall_back_to_a_subfolder_of_the_content_root(string? configured)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kyc:Storage:BasePath"] = configured })
            .Build();

        KycStorageOptions.ResolveBasePath(config, HostEnvironment("/srv/app"))
            .Should().Be(Path.Combine("/srv/app", "kyc-documents"));
    }

    [Fact]
    public async Task Objects_should_be_partitioned_per_tenant_under_the_configured_root()
    {
        var sut = CreateStore();
        using var forA = Jpeg();
        using var forB = Jpeg();

        var a = await sut.StoreAsync(TenantA, KycFileId, KycDocumentKind.Selfie, forA, "image/jpeg", default);
        var b = await sut.StoreAsync(TenantB, KycFileId, KycDocumentKind.Selfie, forB, "image/jpeg", default);

        ObjectFiles().Should().HaveCount(2).And.OnlyContain(f => f.StartsWith(_root, StringComparison.Ordinal));
        TenantFolderOf(a).Should().NotBe(TenantFolderOf(b),
            "offboarding a tenant must be a single directory delete");
    }

    private static IHostEnvironment HostEnvironment(string contentRoot)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return env;
    }

    private string ObjectFileOf(KycStoredDocument stored)
    {
        var objectId = stored.StorageRef.Split('.')[2];
        return ObjectFiles().Single(f => Path.GetFileNameWithoutExtension(f) == objectId);
    }

    private string TenantFolderOf(KycStoredDocument stored) =>
        Path.GetRelativePath(_root, ObjectFileOf(stored)).Split(Path.DirectorySeparatorChar)[0];

    /// <summary>
    /// Stream that reports how much was actually pulled from it, and can pretend not to be
    /// seekable — the only way to prove the size limit is enforced while reading rather than
    /// after buffering everything.
    /// </summary>
    private sealed class CountingStream(byte[] content, bool seekable) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? _inner.Length : throw new NotSupportedException();

        public override long Position
        {
            get => seekable ? _inner.Position : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
