namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// The fifth encrypted store (TEL-B-16). The cases are the ones the other three pay for: the
/// envelope, the tenant binding, and what happens when a file does not authenticate.
/// </summary>
public sealed class EncryptedRecordingStoreTests
{
    /// <summary>A throwaway 32-byte AES-256-GCM key, base64. Test-only, never a real secret.</summary>
    private const string TestKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-5000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-5000-0000-0000-000000000002");

    private static readonly byte[] Audio = Encoding.UTF8.GetBytes("not really audio, but bytes");

    [Fact]
    public async Task A_stored_recording_comes_back_byte_for_byte()
    {
        var backend = new InMemoryBackend();
        var store = Store(backend);

        var stored = await store.StoreAsync(
            TenantA, Guid.NewGuid(), Audio, "audio/mpeg", CancellationToken.None);

        var opened = await store.OpenAsync(TenantA, stored.StorageRef, CancellationToken.None);

        opened.Should().Equal(Audio);
    }

    [Fact]
    public async Task What_reaches_the_backend_is_ciphertext_with_the_format_marker()
    {
        var backend = new InMemoryBackend();
        var store = Store(backend);

        var stored = await store.StoreAsync(
            TenantA, Guid.NewGuid(), Audio, "audio/mpeg", CancellationToken.None);

        var raw = backend.Objects.Values.Should().ContainSingle().Subject;

        // "TRC1" first, so a future layout change is detectable instead of silently garbage.
        Encoding.ASCII.GetString(raw, 0, 4).Should().Be("TRC1");

        // And the audio is nowhere in it.
        raw.Should().NotContainInOrder(Audio);

        stored.Sha256.Should().HaveLength(64);
    }

    [Fact]
    public async Task Two_recordings_of_the_same_call_get_different_references()
    {
        var store = Store(new InMemoryBackend());
        var callId = Guid.NewGuid();

        var first = await store.StoreAsync(
            TenantA, callId, Audio, "audio/mpeg", CancellationToken.None);
        var second = await store.StoreAsync(
            TenantA, callId, Audio, "audio/mpeg", CancellationToken.None);

        // The reference is random and never derived from the call: it appears in the player's URL,
        // so two references must not reveal that they belong to the same conversation — and one
        // built from a call id would let anybody holding it enumerate the neighbours.
        first.StorageRef.Should().NotBe(second.StorageRef);
    }

    [Fact]
    public async Task Another_tenant_cannot_open_a_reference_that_is_not_theirs()
    {
        var store = Store(new InMemoryBackend());

        var stored = await store.StoreAsync(
            TenantA, Guid.NewGuid(), Audio, "audio/mpeg", CancellationToken.None);

        // The tenant segment is derived from the tenant id, so the reference simply does not match
        // — no lookup table, nothing to forget to filter on.
        var opened = await store.OpenAsync(TenantB, stored.StorageRef, CancellationToken.None);

        opened.Should().BeNull();
    }

    [Fact]
    public async Task A_ciphertext_re_filed_under_another_reference_does_not_decrypt()
    {
        var backend = new InMemoryBackend();
        var store = Store(backend);

        var first = await store.StoreAsync(
            TenantA, Guid.NewGuid(), Audio, "audio/mpeg", CancellationToken.None);
        var second = await store.StoreAsync(
            TenantA, Guid.NewGuid(), Encoding.UTF8.GetBytes("another call entirely"),
            "audio/mpeg", CancellationToken.None);

        // Swap the two objects' contents, which is what a mis-restore or a careless ops script
        // does. The reference is fed to GCM as associated data, so the tag check fails.
        var keys = backend.Objects.Keys.ToList();
        (backend.Objects[keys[0]], backend.Objects[keys[1]]) =
            (backend.Objects[keys[1]], backend.Objects[keys[0]]);

        var act = async () =>
            await store.OpenAsync(TenantA, first.StorageRef, CancellationToken.None);

        // Never degraded to "not found": answering "no recording" would have an institution tell a
        // customer, or a regulator, that a conversation was never recorded when a file for it is
        // sitting on the volume.
        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain(RecordingStoreErrors.IntegrityFailure);

        _ = second;
    }

    [Fact]
    public async Task A_malformed_reference_is_refused_before_a_key_is_built()
    {
        var store = Store(new InMemoryBackend());

        foreach (var bad in new[]
        {
            "../../etc/passwd", "r1.short.short", "c1.0123456789abcdef.0123456789abcdef0123456789abcdef",
            string.Empty,
        })
        {
            // Path traversal is handled by never accepting a caller-supplied name, not by
            // sanitising one. Note the third: a CLAIM reference, which is the right shape for a
            // different store and must not open here.
            (await store.OpenAsync(TenantA, bad, CancellationToken.None)).Should().BeNull();
        }
    }

    [Fact]
    public async Task An_unexpected_content_type_is_refused()
    {
        var store = Store(new InMemoryBackend());

        var act = async () => await store.StoreAsync(
            TenantA, Guid.NewGuid(), Audio, "application/x-msdownload", CancellationToken.None);

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain(RecordingStoreErrors.ContentTypeNotAllowed);
    }

    [Fact]
    public async Task A_content_type_with_parameters_is_accepted()
    {
        var store = Store(new InMemoryBackend());

        // A provider sends "audio/mpeg; rate=8000" often enough that comparing the raw header
        // against an allow-list refuses honest files.
        var stored = await store.StoreAsync(
            TenantA, Guid.NewGuid(), Audio, "audio/mpeg; rate=8000", CancellationToken.None);

        stored.ContentType.Should().Be("audio/mpeg");
    }

    [Fact]
    public async Task An_empty_recording_is_refused()
    {
        var store = Store(new InMemoryBackend());

        var act = async () => await store.StoreAsync(
            TenantA, Guid.NewGuid(), [], "audio/mpeg", CancellationToken.None);

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain(RecordingStoreErrors.Empty);
    }

    [Fact]
    public async Task A_recording_over_the_byte_backstop_is_refused()
    {
        var store = Store(new InMemoryBackend(), maxBytes: 16);

        var act = async () => await store.StoreAsync(
            TenantA, Guid.NewGuid(), new byte[17], "audio/wav", CancellationToken.None);

        // The duration ceiling is the cheap refusal, checked before a download starts; this is the
        // backstop for a provider that misreports, because AesGcm is single-shot and the whole
        // plaintext is in the process either way.
        (await act.Should().ThrowAsync<DomainException>())
            .Which.Message.Should().Contain(RecordingStoreErrors.TooLarge);
    }

    [Fact]
    public async Task Deleting_removes_the_object_and_a_second_delete_is_not_a_failure()
    {
        var backend = new InMemoryBackend();
        var store = Store(backend);

        var stored = await store.StoreAsync(
            TenantA, Guid.NewGuid(), Audio, "audio/mpeg", CancellationToken.None);

        (await store.DeleteAsync(TenantA, stored.StorageRef, CancellationToken.None))
            .Should().BeTrue();

        backend.Objects.Should().BeEmpty();

        // The purge job treats false as "the row has to catch up with the volume", not as an
        // error, so this must not throw.
        (await store.DeleteAsync(TenantA, stored.StorageRef, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public void A_missing_key_fails_loudly_and_names_the_setting()
    {
        var act = () => Store(new InMemoryBackend(), key: null);

        // The failure mode this guards is the one the sibling stores document: an unconfigured key
        // surfacing as "ArgumentNullException (Parameter 's')" from Convert.FromBase64String in
        // the middle of a retrieval, naming nothing.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{RecordingStorageOptions.SectionName}:EncryptionKey*");
    }

    [Fact]
    public void A_key_of_the_wrong_length_fails_loudly_too()
    {
        var act = () => Store(new InMemoryBackend(), key: Convert.ToBase64String(new byte[16]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static EncryptedRecordingStore Store(
        IObjectBackend backend, string? key = TestKey, long maxBytes = 50L * 1024 * 1024)
        => new(
            backend,
            Options.Create(new RecordingStorageOptions
            {
                EncryptionKey = key,
                MaxBytes = maxBytes,
            }),
            NullLogger<EncryptedRecordingStore>.Instance);

    /// <summary>
    /// A backend that keeps its objects in a dictionary, so a test can look at exactly what would
    /// have reached a bucket — which is the only way to assert that the audio is not in it.
    /// </summary>
    private sealed class InMemoryBackend : IObjectBackend
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public Task PutAsync(string key, byte[] content, CancellationToken ct)
        {
            Objects[key] = content;

            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, long maxBytes, CancellationToken ct)
            => Task.FromResult(
                Objects.TryGetValue(key, out var bytes) && bytes.Length <= maxBytes ? bytes : null);

        public Task<bool> DeleteAsync(string key, CancellationToken ct)
            => Task.FromResult(Objects.Remove(key));

        public Task<bool> ExistsAsync(string key, CancellationToken ct)
            => Task.FromResult(Objects.ContainsKey(key));

        /// <summary>
        /// Only the object-storage migrator walks keys, and the recording store never does — but
        /// the member is on the interface, so it is implemented honestly rather than throwing: a
        /// double that threw here would fail a future test for a reason unrelated to its subject.
        /// </summary>
        public async IAsyncEnumerable<string> ListAsync(
            string keyPrefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var key in Objects.Keys
                .Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal))
                .ToList())
            {
                ct.ThrowIfCancellationRequested();

                yield return key;
            }

            await Task.CompletedTask;
        }
    }
}
