namespace Sankore.Modules.Integration.Tests.Infrastructure.ClaimStorage;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// The compliance argument for the claim-piece store: a police report is unreadable on the volume,
/// a reference issued to one tenant is useless to another, a crafted reference cannot reach
/// outside the store, and the digest proves the piece behind a transmission was never swapped.
///
/// <para>
/// Over the REAL filesystem backend and not a substitute, for the reason
/// <c>KycDocumentStoreTests</c> gives: most of these assert on what is actually on the volume, and
/// an in-memory backend would prove the store encrypts something rather than proving the piece is
/// unreadable where it is kept.
/// </para>
/// </summary>
public sealed class EncryptedClaimDocumentStoreTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClaimId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sankore-claim-store-tests", Guid.NewGuid().ToString("N"));

    private readonly string _key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private EncryptedClaimDocumentStore CreateStore(long? maxBytes = null, string? key = null)
        => new(
            new LocalObjectBackend(_root, NullLogger<LocalObjectBackend>.Instance),
            Options.Create(new ClaimStorageOptions
            {
                BasePath = _root,
                EncryptionKey = key ?? _key,
                MaxBytes = maxBytes ?? 20L * 1024 * 1024,
            }),
            NullLogger<EncryptedClaimDocumentStore>.Instance);

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private IEnumerable<string> ObjectsOnDisk()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            : [];

    // ── what lands on the volume ────────────────────────────────────────────

    [Fact]
    public async Task A_stored_piece_is_unreadable_where_it_is_kept()
    {
        // The property the whole class exists for. A police report names the insured, the
        // circumstances and often a third party.
        const string report = "Constat: collision avenue Houphouët-Boigny, assuré N'Guessan.";

        var store = CreateStore();

        await store.StoreAsync(TenantA, ClaimId, Content(report), "application/pdf", default);

        var onDisk = ObjectsOnDisk().Should().ContainSingle().Subject;
        var bytes = await File.ReadAllBytesAsync(onDisk);
        var asText = Encoding.UTF8.GetString(bytes);

        asText.Should().NotContain("N'Guessan");
        asText.Should().NotContain("Houphouët");

        // The format marker, so a future layout change is detectable instead of silently garbage.
        Encoding.ASCII.GetString(bytes.AsSpan(0, 4)).Should().Be("ICD1");

        // And the extension, which is the on-disk contract this store does NOT share with its two
        // siblings.
        onDisk.Should().EndWith(".icd");
    }

    [Fact]
    public async Task A_stored_piece_comes_back_byte_for_byte()
    {
        var store = CreateStore();
        var original = RandomNumberGenerator.GetBytes(4096);

        var stored = await store.StoreAsync(
            TenantA, ClaimId, new MemoryStream(original), "image/jpeg", default);

        var reopened = await store.OpenAsync(TenantA, stored.StorageRef, default);

        reopened.Should().Equal(original);
    }

    [Fact]
    public async Task The_digest_is_of_the_PLAINTEXT()
    {
        // It goes into ux_ins_claim_document_digest, so it is what makes the same file twice a
        // refusal. A digest of the ciphertext would differ on every upload — the nonce is fresh —
        // and the duplicate rule would never fire.
        var store = CreateStore();
        var payload = Encoding.UTF8.GetBytes("devis de réparation");

        var first = await store.StoreAsync(
            TenantA, ClaimId, new MemoryStream(payload), "application/pdf", default);

        var second = await store.StoreAsync(
            TenantA, ClaimId, new MemoryStream(payload), "application/pdf", default);

        first.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        second.Sha256.Should().Be(first.Sha256, "the same file has the same digest");
        second.StorageRef.Should().NotBe(first.StorageRef, "but it is a different object");
    }

    // ── tenant isolation ────────────────────────────────────────────────────

    [Fact]
    public async Task A_reference_issued_to_one_tenant_is_useless_to_another()
    {
        var store = CreateStore();

        var stored = await store.StoreAsync(
            TenantA, ClaimId, Content("constat"), "application/pdf", default);

        (await store.OpenAsync(TenantB, stored.StorageRef, default)).Should().BeNull();
        (await store.DeleteAsync(TenantB, stored.StorageRef, default)).Should().BeFalse();

        // And A still has it: B's attempt changed nothing.
        (await store.OpenAsync(TenantA, stored.StorageRef, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Re_filing_a_ciphertext_under_another_reference_does_not_make_it_decrypt()
    {
        // The tenant id AND the reference are GCM associated data, so moving an object cannot make
        // it readable elsewhere — the tag check fails. This is the property that makes the key
        // prefix a guarantee rather than a convention.
        var store = CreateStore();

        var mine = await store.StoreAsync(
            TenantA, ClaimId, Content("constat"), "application/pdf", default);

        var other = await store.StoreAsync(
            TenantA, ClaimId, Content("autre"), "application/pdf", default);

        var minePath = ObjectsOnDisk().Single(p => p.Contains(mine.StorageRef.Split('.')[2], StringComparison.Ordinal));
        var otherPath = ObjectsOnDisk().Single(p => p.Contains(other.StorageRef.Split('.')[2], StringComparison.Ordinal));

        // Swap the bytes: the object under `other`'s key is now `mine`'s ciphertext.
        File.Copy(minePath, otherPath, overwrite: true);

        var act = async () => await store.OpenAsync(TenantA, other.StorageRef, default);

        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.Message.Contains(ClaimDocumentErrors.IntegrityFailure, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_key_is_an_INTEGRITY_failure_and_never_a_missing_object()
    {
        // The object exists and does not authenticate: a swapped file, a bit-rotted volume or the
        // wrong key. Degrading that to "not found" would quietly drop evidence from a dossier, or
        // transmit to an insurer a document we can no longer vouch for.
        var stored = await CreateStore().StoreAsync(
            TenantA, ClaimId, Content("constat"), "application/pdf", default);

        var otherKey = CreateStore(key: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        var act = async () => await otherKey.OpenAsync(TenantA, stored.StorageRef, default);

        await act.Should().ThrowAsync<DomainException>();
    }

    // ── crafted references ──────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("not-a-ref")]
    [InlineData("k1.0123456789abcdef.0123456789abcdef0123456789abcdef")]
    [InlineData("c1.0123456789abcdef.0123456789abcdef0123456789abcdef/../../etc/passwd")]
    [InlineData("c1.0123456789abcdef.0123456789abcdef0123456789abcdef\n/../../etc/passwd")]
    [InlineData("../../etc/passwd")]
    public async Task A_crafted_reference_cannot_reach_outside_the_store(string crafted)
    {
        // Path traversal is handled by never accepting a caller-supplied name, not by sanitising
        // one — and the \z anchor is why the newline case is refused too. The third case is a KYC
        // reference: the two keyspaces do not interoperate.
        var store = CreateStore();

        (await store.OpenAsync(TenantA, crafted, default)).Should().BeNull();
        (await store.DeleteAsync(TenantA, crafted, default)).Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_but_well_shaped_reference_is_simply_absent()
    {
        // Null and not an exception: a reference travels through URLs, so "not yours" and "does
        // not exist" must be indistinguishable.
        var store = CreateStore();

        var shaped =
            $"c1.{Convert.ToHexString(SHA256.HashData(TenantA.ToByteArray()))[..16].ToLowerInvariant()}."
            + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        (await store.OpenAsync(TenantA, shaped, default)).Should().BeNull();
    }

    // ── the refusals ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/zip")]
    [InlineData("application/x-msdownload")]
    [InlineData("text/html")]
    [InlineData("")]
    public async Task A_content_type_that_is_not_a_piece_is_refused(string contentType)
    {
        // An allow-list, because an executable refused by name is an executable renamed — and no
        // archive, because its contents are what a scan would have to walk and this platform does
        // not.
        var store = CreateStore();

        var act = async () => await store.StoreAsync(
            TenantA, ClaimId, Content("x"), contentType, default);

        (await act.Should().ThrowAsync<DomainException>()).Which
            .Message.Should().Contain(ClaimDocumentErrors.ContentTypeNotAllowed);
    }

    [Fact]
    public async Task A_content_type_with_parameters_is_still_accepted()
    {
        // A browser sends "image/jpeg; charset=UTF-8" often enough that comparing the raw header
        // against an allow-list would refuse honest uploads.
        var store = CreateStore();

        var stored = await store.StoreAsync(
            TenantA, ClaimId, Content("x"), "image/jpeg; charset=UTF-8", default);

        stored.ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task An_oversized_piece_is_refused_without_being_written()
    {
        var store = CreateStore(maxBytes: 64);

        var act = async () => await store.StoreAsync(
            TenantA, ClaimId, new MemoryStream(new byte[256]), "image/png", default);

        (await act.Should().ThrowAsync<DomainException>()).Which
            .Message.Should().Contain(ClaimDocumentErrors.TooLarge);

        ObjectsOnDisk().Should().BeEmpty("nothing reached the backend");
    }

    [Fact]
    public async Task An_empty_piece_is_refused()
    {
        var store = CreateStore();

        var act = async () => await store.StoreAsync(
            TenantA, ClaimId, new MemoryStream([]), "image/png", default);

        (await act.Should().ThrowAsync<DomainException>()).Which
            .Message.Should().Contain(ClaimDocumentErrors.Empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64")]
    // Valid Base64, 5 bytes — the case a hand-typed key actually produces.
    [InlineData("c2hvcnQ=")]
    public void A_missing_or_malformed_key_fails_at_construction_naming_the_setting(string? key)
    {
        // Eagerly, as both sibling stores do: a key read lazily surfaces as
        // "ArgumentNullException (Parameter 's')" out of Convert.FromBase64String in the middle of
        // an upload, with nothing in the message naming the setting.
        //
        // Constructed directly and NOT through CreateStore, which defaults a null key to the
        // valid one — going through the helper made the null case assert nothing at all.
        var act = () => new EncryptedClaimDocumentStore(
            new LocalObjectBackend(_root, NullLogger<LocalObjectBackend>.Instance),
            Options.Create(new ClaimStorageOptions { BasePath = _root, EncryptionKey = key }),
            NullLogger<EncryptedClaimDocumentStore>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{ClaimStorageOptions.SectionName}:EncryptionKey*");
    }

    // ── deletion ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_twice_is_idempotent()
    {
        // So the upload's own cleanup and the retention sweep can both run without coordinating.
        var store = CreateStore();

        var stored = await store.StoreAsync(
            TenantA, ClaimId, Content("constat"), "application/pdf", default);

        (await store.DeleteAsync(TenantA, stored.StorageRef, default)).Should().BeTrue();
        (await store.DeleteAsync(TenantA, stored.StorageRef, default)).Should().BeFalse();
        (await store.OpenAsync(TenantA, stored.StorageRef, default)).Should().BeNull();
    }
}
