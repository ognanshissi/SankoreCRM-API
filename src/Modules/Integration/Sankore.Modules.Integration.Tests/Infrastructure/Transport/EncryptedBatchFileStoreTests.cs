namespace Sankore.Modules.Integration.Tests.Infrastructure.Transport;

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Infrastructure.BatchStorage;
using Sankore.Modules.Integration.Tests.Features.Batch.Outbound;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// Criterion 5, first half: the file is encrypted at rest.
///
/// <para>
/// Lives under <c>Infrastructure/Transport/</c> because that is this slice's infrastructure test
/// folder; the type it covers is in <c>Infrastructure/BatchStorage/</c>.
/// </para>
/// </summary>
public sealed class EncryptedBatchFileStoreTests
{
    private static readonly Guid TenantA = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly byte[] Plaintext =
        Encoding.UTF8.GetBytes("command_id;crm_id\r\nAWA OUATTARA;CI-0012345678\r\n");

    [Fact]
    public async Task The_stored_bytes_are_not_the_plaintext()
    {
        var (store, backend) = Store();

        var reference = await store.StoreAsync(TenantA, Plaintext, CancellationToken.None);

        var stored = backend.Peek(backend.Keys.Single())!;

        stored.Should().NotEqual(Plaintext);

        // And it does not merely differ — the plaintext must not be findable inside it. A store
        // that prefixed a header and wrote the body in clear would pass a NotEqual on its own.
        Encoding.UTF8.GetString(stored).Should().NotContain("AWA OUATTARA");
        Encoding.UTF8.GetString(stored).Should().NotContain("CI-0012345678");

        // It is self-describing, so a future layout change is detectable rather than garbage.
        Encoding.ASCII.GetString(stored, 0, 4).Should().Be("IBF1");

        (await store.OpenAsync(TenantA, reference, CancellationToken.None))
            .Should().Equal(Plaintext, "and it round-trips");
    }

    [Fact]
    public async Task A_ciphertext_written_for_one_tenant_cannot_be_read_under_anothers_reference()
    {
        var backend = new InMemoryObjectBackend();

        var storeA = StoreOver(backend);
        var storeB = StoreOver(backend);

        var referenceA = await storeA.StoreAsync(TenantA, Plaintext, CancellationToken.None);
        var objectKey = backend.Keys.Single();

        // Tenant B asks for tenant A's reference. The tenant segment of the reference is derived
        // from the tenant id, so it does not even resolve to a key — a mis-scoped read is a
        // missing object, never a leak.
        (await storeB.OpenAsync(TenantB, referenceA, CancellationToken.None)).Should().BeNull();

        // Now the harder case: B re-files A's ciphertext under a reference of its OWN, which does
        // resolve. This is the replay the associated data exists to stop.
        var referenceB = await storeB.StoreAsync(
            TenantB, Encoding.UTF8.GetBytes("placeholder"), CancellationToken.None);

        var bObjectKey = backend.Keys.Single(k => k != objectKey);
        backend.Overwrite(bObjectKey, backend.Peek(objectKey)!);

        var replay = async () => await storeB.OpenAsync(TenantB, referenceB, CancellationToken.None);

        await replay.Should().ThrowAsync<DomainException>()
            .Where(e => e.Message.Contains("INTEGRATION_BATCH_FILE_INTEGRITY_FAILURE"),
                "the tenant id and the reference are bound as associated data, so the tag check "
                + "fails rather than handing one institution another's portfolio");
    }

    [Fact]
    public async Task A_tampered_object_fails_loudly_rather_than_reading_as_absent()
    {
        var (store, backend) = Store();

        var reference = await store.StoreAsync(TenantA, Plaintext, CancellationToken.None);
        var key = backend.Keys.Single();

        var tampered = backend.Peek(key)!.ToArray();
        tampered[^1] ^= 0xFF;
        backend.Overwrite(key, tampered);

        var read = async () => await store.OpenAsync(TenantA, reference, CancellationToken.None);

        // Never degraded to "not found": the object exists and does not authenticate, which means
        // a swapped file, a bit-rotted volume or the wrong key — all three are incidents.
        await read.Should().ThrowAsync<DomainException>()
            .Where(e => e.Message.Contains("INTEGRATION_BATCH_FILE_INTEGRITY_FAILURE"));
    }

    [Fact]
    public async Task Another_key_cannot_open_the_objects()
    {
        var backend = new InMemoryObjectBackend();

        var reference = await StoreOver(backend).StoreAsync(
            TenantA, Plaintext, CancellationToken.None);

        var withOtherKey = StoreOver(backend, OutboundBatchTestContext.OtherTestKey);

        var read = async () => await withOtherKey.OpenAsync(
            TenantA, reference, CancellationToken.None);

        await read.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task The_delete_removes_the_content()
    {
        var (store, backend) = Store();

        var reference = await store.StoreAsync(TenantA, Plaintext, CancellationToken.None);

        (await store.DeleteAsync(TenantA, reference, CancellationToken.None)).Should().BeTrue();
        backend.Keys.Should().BeEmpty();

        // Idempotent: a second purge of the same reference is not an error.
        (await store.DeleteAsync(TenantA, reference, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_malformed_reference_is_refused_without_touching_the_backend()
    {
        var (store, _) = Store();

        foreach (var malformed in new[]
                 {
                     "", "   ", "../../etc/passwd", "b1.deadbeef.xyz",
                     "b1.0123456789abcdef.0123456789abcdef0123456789abcdef/../x",
                     "b1.0123456789abcdef.0123456789abcdef0123456789abcdef\n/../etc/passwd",
                 })
        {
            (await store.OpenAsync(TenantA, malformed, CancellationToken.None))
                .Should().BeNull($"'{malformed}' is not a reference this store ever issued");
        }
    }

    [Fact]
    public async Task An_empty_or_oversized_file_is_refused_by_name()
    {
        var (store, _) = Store(maxBytes: 64);

        var empty = async () => await store.StoreAsync(TenantA, [], CancellationToken.None);
        await empty.Should().ThrowAsync<DomainException>()
            .Where(e => e.Message.Contains("INTEGRATION_BATCH_FILE_EMPTY"));

        var tooLarge = async () => await store.StoreAsync(
            TenantA, new byte[65], CancellationToken.None);

        await tooLarge.Should().ThrowAsync<DomainException>()
            .Where(e => e.Message.Contains("INTEGRATION_BATCH_FILE_TOO_LARGE"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64-!!")]
    [InlineData("c2hvcnQ=")]
    public void A_missing_or_wrong_sized_key_fails_at_construction_naming_the_setting(string? key)
    {
        // The documented history: a key read lazily surfaced as "ArgumentNullException
        // (Parameter 's')" from Convert.FromBase64String in the middle of a request, with nothing
        // in the message naming the setting.
        var build = () => new EncryptedBatchFileStore(
            new InMemoryObjectBackend(),
            Options.Create(new IntegrationBatchStorageOptions { EncryptionKey = key }),
            NullLogger<EncryptedBatchFileStore>.Instance);

        build.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("Integration:Batch:Storage:EncryptionKey"));
    }

    private static (IBatchFileStore Store, InMemoryObjectBackend Backend) Store(
        long maxBytes = 32L * 1024 * 1024)
    {
        var backend = new InMemoryObjectBackend();
        return (StoreOver(backend, maxBytes: maxBytes), backend);
    }

    private static IBatchFileStore StoreOver(
        InMemoryObjectBackend backend,
        string key = OutboundBatchTestContext.TestKey,
        long maxBytes = 32L * 1024 * 1024)
        => new EncryptedBatchFileStore(
            backend,
            Options.Create(new IntegrationBatchStorageOptions
            {
                EncryptionKey = key,
                MaxBytes = maxBytes,
            }),
            NullLogger<EncryptedBatchFileStore>.Instance);
}
