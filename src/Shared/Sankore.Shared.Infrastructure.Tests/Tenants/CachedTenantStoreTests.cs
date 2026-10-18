namespace Sankore.Shared.Infrastructure.Tests.Tenants;

using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The FQDN cache may keep an origin's tenant id for a day; it must never keep its STATUS. It used
/// to store the whole TenantInfo for 24 hours, so a tenant deactivated in the admin kept booting as
/// active on its own domain for a day.
/// </summary>
public sealed class CachedTenantStoreTests
{
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Fqdn = "https://imf.example.com";

    private static TenantInfo Tenant(bool active) => new(TenantId, "IMF", Fqdn, active, false, null, null);

    [Fact]
    public async Task A_deactivation_is_seen_through_the_fqdn_lookup_once_the_short_cache_expires()
    {
        var inner = Substitute.For<ITenantStore>();
        inner.GetByFqdnAsync(Fqdn, Arg.Any<CancellationToken>()).Returns(Tenant(active: true));
        inner.GetAsync(TenantId, Arg.Any<CancellationToken>()).Returns(Tenant(active: false));

        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new CachedTenantStore(inner, cache, Options.Create(new TenantStoreOptions
        {
            CacheTtl = TimeSpan.FromMilliseconds(50),
            FqdnCacheTtl = TimeSpan.FromHours(24),
        }));

        (await store.GetByFqdnAsync(Fqdn))!.IsActive.Should().BeTrue();

        await Task.Delay(120);

        // The id mapping is still cached (no second by-FQDN call), but the status is re-read.
        (await store.GetByFqdnAsync(Fqdn))!.IsActive.Should().BeFalse();
        await inner.Received(1).GetByFqdnAsync(Fqdn, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_origin_is_not_cached()
    {
        var inner = Substitute.For<ITenantStore>();
        inner.GetByFqdnAsync(Fqdn, Arg.Any<CancellationToken>()).Returns((TenantInfo?)null, Tenant(active: true));

        var store = new CachedTenantStore(
            inner,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            Options.Create(new TenantStoreOptions()));

        (await store.GetByFqdnAsync(Fqdn)).Should().BeNull();
        (await store.GetByFqdnAsync(Fqdn)).Should().NotBeNull();
    }
}
