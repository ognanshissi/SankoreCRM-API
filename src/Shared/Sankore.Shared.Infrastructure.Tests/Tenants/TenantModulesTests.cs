namespace Sankore.Shared.Infrastructure.Tests.Tenants;

using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Paid modules as the API reads them: off the cached TenantInfo, failing OPEN whenever the list
/// is unknown, and enforced on routes by <see cref="TenantModuleEndpointFilter"/>.
/// </summary>
public sealed class TenantModulesTests
{
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static TenantInfo Tenant(IReadOnlyList<string>? modules, IReadOnlyList<string>? readOnly = null) =>
        new(TenantId, "IMF", "https://imf.example.com", true, false, null, null, "fr", modules, readOnly);

    private static TenantModulesService Service(TenantInfo? tenant)
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAsync(TenantId, Arg.Any<CancellationToken>()).Returns(tenant);
        var context = Substitute.For<ITenantContext>();
        context.CurrentTenantId.Returns(TenantId);
        return new TenantModulesService(store, context);
    }

    [Fact]
    public async Task A_module_in_force_is_enabled_and_one_missing_from_the_list_is_not()
    {
        var service = Service(Tenant([TenantModules.Insurance]));

        (await service.IsEnabledAsync(TenantModules.Insurance)).Should().BeTrue();
        (await service.IsEnabledAsync(TenantModules.BankingMicrofinance)).Should().BeFalse();
        (await service.ActiveAsync(TenantId)).Should().Equal(TenantModules.Insurance);
    }

    [Fact]
    public async Task An_empty_list_is_the_socle_alone()
    {
        var service = Service(Tenant([]));

        (await service.IsEnabledAsync(TenantModules.Insurance)).Should().BeFalse();
        (await service.ActiveAsync(TenantId)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unknown_list_or_an_unknown_tenant_reads_as_every_module(bool tenantFound)
    {
        var service = Service(tenantFound ? Tenant(null) : null);

        (await service.IsEnabledAsync(TenantModules.Insurance)).Should().BeTrue();
        (await service.IsEnabledAsync(TenantModules.BankingMicrofinance)).Should().BeTrue();
        (await service.ActiveAsync(TenantId)).Should().Equal(TenantModules.All);
    }

    [Fact]
    public async Task A_TenantInfo_cached_before_modules_existed_reads_as_every_module()
    {
        // Exactly what the previous version wrote into Redis: no activeModules property at all.
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var legacy = JsonSerializer.Serialize(new
        {
            id = TenantId, name = "IMF", fqdn = "https://imf.example.com", isActive = true,
            isMaintenance = false, trialExpiresAt = (DateTimeOffset?)null, blockedAt = (DateTimeOffset?)null,
            defaultLanguage = "fr",
        });
        await cache.SetStringAsync($"tenant:id:{TenantId}", legacy);

        var store = new CachedTenantStore(Substitute.For<ITenantStore>(), cache, Options.Create(new TenantStoreOptions()));
        var tenant = await store.GetAsync(TenantId);

        tenant!.ActiveModules.Should().BeNull();
        tenant.HasModule(TenantModules.Insurance).Should().BeTrue();
    }

    [Fact]
    public async Task Invalidating_drops_the_cached_status_so_the_next_read_reaches_the_registry()
    {
        var inner = Substitute.For<ITenantStore>();
        inner.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Tenant([TenantModules.Insurance]), Tenant([]));
        var store = new CachedTenantStore(inner,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            Options.Create(new TenantStoreOptions { CacheTtl = TimeSpan.FromHours(1) }));

        (await store.GetAsync(TenantId))!.ActiveModules.Should().Equal(TenantModules.Insurance);
        (await store.GetAsync(TenantId))!.ActiveModules.Should().Equal([TenantModules.Insurance], "it is still cached");

        await store.InvalidateAsync(TenantId);

        (await store.GetAsync(TenantId))!.ActiveModules.Should().BeEmpty();
    }

    private static Task<(bool Reached, object? Result)> FilterAsync(TenantInfo? tenant, params string[] anyOf)
        => FilterAsync(HttpMethods.Get, tenant, anyOf);

    private static async Task<(bool Reached, object? Result)> FilterAsync(string method, TenantInfo? tenant, params string[] anyOf)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantModules>(Service(tenant));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = method;

        var reached = false;
        var result = await new TenantModuleEndpointFilter(anyOf).InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ => { reached = true; return ValueTask.FromResult<object?>(Results.Ok()); });
        return (reached, result);
    }

    [Fact]
    public async Task The_filter_lets_a_tenant_with_the_module_through()
    {
        (await FilterAsync(Tenant([TenantModules.BankingMicrofinance]), TenantModules.BankingMicrofinance))
            .Reached.Should().BeTrue();
    }

    [Fact]
    public async Task The_filter_answers_403_TENANT_MODULE_DISABLED_naming_the_module()
    {
        var (reached, result) = await FilterAsync(Tenant([TenantModules.Insurance]), TenantModules.BankingMicrofinance);

        reached.Should().BeFalse();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var body = JsonSerializer.Serialize(((IValueHttpResult)result!).Value);
        body.Should().Contain(TenantModuleErrors.Disabled).And.Contain(TenantModules.BankingMicrofinance);
    }

    [Fact]
    public async Task A_shared_surface_opens_with_either_module()
    {
        string[] shared = [TenantModules.BankingMicrofinance, TenantModules.Insurance];

        (await FilterAsync(Tenant([TenantModules.Insurance]), shared)).Reached.Should().BeTrue();
        (await FilterAsync(Tenant([]), shared)).Reached.Should().BeFalse();
    }

    [Fact]
    public async Task A_deactivated_module_stays_readable_and_refuses_every_write()
    {
        var deactivated = Tenant([], readOnly: [TenantModules.Insurance]);

        (await FilterAsync(HttpMethods.Get, deactivated, TenantModules.Insurance)).Reached.Should().BeTrue();
        foreach (var write in new[] { HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete, HttpMethods.Patch })
            (await FilterAsync(write, deactivated, TenantModules.Insurance)).Reached.Should().BeFalse(write);

        (await FilterAsync(HttpMethods.Get, deactivated, TenantModules.BankingMicrofinance)).Reached
            .Should().BeFalse("a module never subscribed has nothing to read");
    }

    [Fact]
    public async Task The_read_only_list_is_empty_when_the_modules_are_unknown()
    {
        (await Service(Tenant(null, readOnly: [TenantModules.Insurance])).ReadOnlyAsync(TenantId))
            .Should().BeEmpty("an unknown list reads as every module in force");
        (await Service(Tenant([], readOnly: [TenantModules.Insurance])).ReadOnlyAsync(TenantId))
            .Should().Equal(TenantModules.Insurance);
    }

    [Fact]
    public async Task The_filter_fails_open_when_the_registry_is_silent()
    {
        (await FilterAsync(null, TenantModules.Insurance)).Reached.Should().BeTrue();
    }

    [Fact]
    public void A_route_cannot_be_gated_on_something_that_is_not_a_module()
    {
        // Telephony is socle: naming it is a programming error, caught when the route is mapped.
        var act = () => TenantModuleEndpointFilterExtensions.RequireTenantModule(
            Substitute.For<Microsoft.AspNetCore.Builder.IEndpointConventionBuilder>(), "Telephony");
        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>Every product category belongs to exactly one module, or is the socle's Standard.</summary>
public sealed class ProductCategoryModuleTests
{
    [Fact]
    public void Every_category_has_a_module_or_is_Standard()
    {
        foreach (var category in Enum.GetValues<ProductCategory>())
        {
            var act = () => ProductCategories.ModuleOf(category);
            act.Should().NotThrow($"{category} must be placed in a module or declared socle");
        }
    }

    [Fact]
    public void The_split_is_the_one_the_product_owner_decided()
    {
        ProductCategories.ModuleOf(ProductCategory.Standard).Should().BeNull();

        foreach (var c in new[] { ProductCategory.Insurance, ProductCategory.HealthInsurance, ProductCategory.ForecastInsurance })
            ProductCategories.ModuleOf(c).Should().Be(TenantModules.Insurance);

        foreach (var c in new[]
                 {
                     ProductCategory.Loan, ProductCategory.Savings, ProductCategory.CurrentAccount,
                     ProductCategory.Tontine, ProductCategory.GroupCredit, ProductCategory.Agriculture,
                 })
            ProductCategories.ModuleOf(c).Should().Be(TenantModules.BankingMicrofinance);

        ProductCategories.Insurance.Intersect(ProductCategories.BankingMicrofinance).Should().BeEmpty();
    }
}
