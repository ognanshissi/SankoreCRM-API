namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Products.SetBeneficiaryPolicy;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Covers the 5-minute catalogue cache BNF-B-06 asks for, and — the part that actually breaks —
/// its invalidation.
/// </summary>
public sealed class ProductCatalogCacheTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly RecordingCache _cache = new();

    public ProductCatalogCacheTests() => _factory = new TestAdminDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private ProductCatalogCache Catalog() => new(_cache);

    private AdministrationModuleFacade Facade()
        => new(_factory.CreateContext(), Catalog());

    private async Task<ProductSpeciality> SeedAsync(string code, string name)
    {
        var product = ProductSpeciality.Create(_tenant, name, code, ProductCategory.Insurance);

        await using var db = _factory.CreateContext();
        db.ProductSpecialities.Add(product);
        await db.SaveChangesAsync(CancellationToken.None);
        return product;
    }

    // ── Caching ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_policy_read_is_served_from_the_cache_the_second_time()
    {
        var product = await SeedAsync("SANTE-01", "Assurance santé");

        await Facade().GetBeneficiaryPolicyAsync(_tenant, product.Id, CancellationToken.None);
        _cache.Writes.Should().Contain(ProductCatalogCache.PolicyKey(_tenant, product.Id));

        _cache.Reads.Clear();
        var second = await Facade().GetBeneficiaryPolicyAsync(
            _tenant, product.Id, CancellationToken.None);

        second.Should().NotBeNull();
        _cache.Reads.Should().Contain(ProductCatalogCache.PolicyKey(_tenant, product.Id));
    }

    [Fact]
    public async Task Label_resolution_caches_the_tenants_active_catalogue()
    {
        // This read is on the capture path — once per lead without a cache, once per five minutes
        // with one.
        await SeedAsync("CRED-COM-01", "Crédit commerçant");

        await Facade().ResolveProductLineAsync(_tenant, "CRED-COM-01", CancellationToken.None);

        _cache.Writes.Should().Contain(ProductCatalogCache.CatalogueKey(_tenant));
    }

    [Fact]
    public async Task A_cached_catalogue_still_resolves_the_same_line()
    {
        // Round-trips through JSON, so a shape change in CatalogueEntry would surface here rather
        // than as leads silently resolving to nothing for five minutes after a deploy.
        var product = await SeedAsync("CRED-COM-01", "Crédit commerçant");
        var facade = Facade();

        await facade.ResolveProductLineAsync(_tenant, "CRED-COM-01", CancellationToken.None);

        (await facade.ResolveProductLineAsync(_tenant, "CREDIT COMMERCANT", CancellationToken.None))
            .Should().Be(product.Id);
    }

    [Fact]
    public async Task One_tenants_cache_entry_never_answers_for_another()
    {
        var product = await SeedAsync("SANTE-01", "Assurance santé");

        ProductCatalogCache.PolicyKey(_tenant, product.Id)
            .Should().NotBe(ProductCatalogCache.PolicyKey(Guid.NewGuid(), product.Id));
        ProductCatalogCache.CatalogueKey(_tenant)
            .Should().NotBe(ProductCatalogCache.CatalogueKey(Guid.NewGuid()));
    }

    // ── Invalidation: the part that breaks ────────────────────────────────

    [Fact]
    public async Task Setting_a_policy_drops_both_cache_entries()
    {
        // The failure this test exists for: a reader and a writer disagreeing about the key format
        // makes a cache that NEVER invalidates, and the symptom is a five-minute delay that looks
        // like flakiness rather than like a bug.
        var product = await SeedAsync("SANTE-01", "Assurance santé");

        await Facade().GetBeneficiaryPolicyAsync(_tenant, product.Id, CancellationToken.None);
        await Facade().ResolveProductLineAsync(_tenant, "SANTE-01", CancellationToken.None);
        _cache.Removals.Clear();

        await using var db = _factory.CreateContext();
        var result = await new SetBeneficiaryPolicyHandler(db, Catalog()).Handle(
            new SetBeneficiaryPolicyCommand(
                ProductId: product.Id,
                HouseholdEnabled: true,
                AllowMultipleHoldings: false,
                AllowedRoles: [new DesignationRoleInput("AYANTDROITSANTE", "Ayant droit santé")]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _cache.Removals.Should().Contain(ProductCatalogCache.PolicyKey(_tenant, product.Id));
        _cache.Removals.Should().Contain(ProductCatalogCache.CatalogueKey(_tenant),
            "a product's code or name changing moves what a label resolves to");
    }

    [Fact]
    public async Task The_new_policy_is_what_the_next_read_returns()
    {
        var product = await SeedAsync("SANTE-01", "Assurance santé");

        await Facade().GetBeneficiaryPolicyAsync(_tenant, product.Id, CancellationToken.None);

        await using (var db = _factory.CreateContext())
        {
            await new SetBeneficiaryPolicyHandler(db, Catalog()).Handle(
                new SetBeneficiaryPolicyCommand(
                    ProductId: product.Id,
                    HouseholdEnabled: true,
                    AllowMultipleHoldings: false,
                    MaxChildren: 6,
                    AllowedRoles: [new DesignationRoleInput("AYANTDROITSANTE", "Ayant droit santé")]),
                CancellationToken.None);
        }

        var after = await Facade().GetBeneficiaryPolicyAsync(
            _tenant, product.Id, CancellationToken.None);

        after!.HouseholdEnabled.Should().BeTrue();
        after.MaxChildren.Should().Be(6);
        after.AllowMultipleHoldings.Should().BeFalse();
        after.AllowedRoles.Should().ContainSingle();
    }

    [Fact]
    public async Task A_broken_cache_costs_a_query_not_the_request()
    {
        // Redis unreachable, or holding a payload written by an older shape of the record.
        var product = await SeedAsync("SANTE-01", "Assurance santé");
        var throwing = new AdministrationModuleFacade(
            _factory.CreateContext(), new ProductCatalogCache(new ThrowingCache()));

        var dto = await throwing.GetBeneficiaryPolicyAsync(
            _tenant, product.Id, CancellationToken.None);

        dto.Should().NotBeNull("a cache is an optimisation, not a dependency");

        (await throwing.ResolveProductLineAsync(_tenant, "SANTE-01", CancellationToken.None))
            .Should().Be(product.Id);
    }

    [Fact]
    public async Task No_cache_at_all_still_works()
    {
        // The unit-test and Redis-down configuration: ProductCatalogCache is built with a null
        // IDistributedCache and every path falls through to the query.
        var product = await SeedAsync("SANTE-01", "Assurance santé");
        var facade = new AdministrationModuleFacade(
            _factory.CreateContext(), new ProductCatalogCache());

        (await facade.GetBeneficiaryPolicyAsync(_tenant, product.Id, CancellationToken.None))
            .Should().NotBeNull();
        (await facade.ResolveProductLineAsync(_tenant, "SANTE-01", CancellationToken.None))
            .Should().Be(product.Id);
    }

    // ── Doubles ───────────────────────────────────────────────────────────

    /// <summary>
    /// A working in-memory <see cref="IDistributedCache"/> that also records which keys were read,
    /// written and removed — the key format being shared between reader and writer is the thing
    /// worth asserting, and an opaque cache cannot show it.
    /// </summary>
    private sealed class RecordingCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        internal List<string> Reads { get; } = [];
        internal List<string> Writes { get; } = [];
        internal List<string> Removals { get; } = [];

        public byte[]? Get(string key)
        {
            Reads.Add(key);
            return _store.TryGetValue(key, out var value) ? value : null;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => Task.FromResult(Get(key));

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key)
        {
            Removals.Add(key);
            _store.Remove(key);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Writes.Add(key);
            _store[key] = value;
        }

        public Task SetAsync(
            string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("cache down");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("cache down");
        public void Refresh(string key) => throw new InvalidOperationException("cache down");
        public Task RefreshAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("cache down");
        public void Remove(string key) => throw new InvalidOperationException("cache down");
        public Task RemoveAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("cache down");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => throw new InvalidOperationException("cache down");
        public Task SetAsync(
            string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
            => throw new InvalidOperationException("cache down");
    }
}
