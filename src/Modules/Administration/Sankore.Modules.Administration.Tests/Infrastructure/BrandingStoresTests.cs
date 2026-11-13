namespace Sankore.Modules.Administration.Tests.Infrastructure;

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure.Branding;
using Sankore.Shared.ObjectStorage;
using Xunit;

public sealed class BrandingStoresTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"branding-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalObjectBackend Backend(string concern) =>
        new(Path.Combine(_root, concern), NullLogger<LocalObjectBackend>.Instance);

    private EncryptedUserAvatarStore Avatars() => new(
        Backend(UserAvatarStorage.Concern),
        Options.Create(new UserAvatarStorageOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));

    [Fact]
    public async Task A_logo_is_stored_in_clear_and_read_back_by_its_own_tenant_only()
    {
        var store = new CompanyLogoStore(Backend(CompanyLogoStorage.Concern));
        var tenant = Guid.NewGuid();

        var storageRef = await store.StoreAsync(tenant, Png, default);

        (await store.OpenAsync(tenant, storageRef, default)).Should().Equal(Png);
        (await store.OpenAsync(Guid.NewGuid(), storageRef, default)).Should().BeNull();
        // In clear on purpose: it is public. The object is the image itself.
        Directory.EnumerateFiles(_root, "*.logo", SearchOption.AllDirectories).Single()
            .Should().Match(p => File.ReadAllBytes(p).SequenceEqual(Png));
    }

    [Fact]
    public async Task A_photo_is_encrypted_at_rest_and_read_back_by_its_own_tenant_only()
    {
        var store = Avatars();
        var tenant = Guid.NewGuid();

        var storageRef = await store.StoreAsync(tenant, Png, default);

        (await store.OpenAsync(tenant, storageRef, default)).Should().Equal(Png);
        (await store.OpenAsync(Guid.NewGuid(), storageRef, default)).Should().BeNull();
        var stored = File.ReadAllBytes(Directory.EnumerateFiles(_root, "*.uav", SearchOption.AllDirectories).Single());
        stored[..4].Should().Equal("UAV1"u8.ToArray());
        stored.AsSpan().IndexOf(Png.AsSpan()).Should().Be(-1);
    }

    [Fact]
    public async Task Deleting_removes_the_object()
    {
        var store = Avatars();
        var tenant = Guid.NewGuid();
        var storageRef = await store.StoreAsync(tenant, Png, default);

        (await store.DeleteAsync(tenant, storageRef, default)).Should().BeTrue();

        (await store.OpenAsync(tenant, storageRef, default)).Should().BeNull();
    }

    [Fact]
    public async Task Without_a_store_a_photo_still_in_the_database_is_read_and_a_moved_one_is_not()
    {
        var moved = UserAvatar.Create(Guid.NewGuid(), Guid.NewGuid(), "a1.0000000000000000.00000000000000000000000000000000", "image/png");

        (await BrandingContent.AvatarAsync(store: null, moved, default)).Should().BeNull();
        (await BrandingContent.AvatarAsync(store: null, avatar: null, default)).Should().BeNull();
    }
}
