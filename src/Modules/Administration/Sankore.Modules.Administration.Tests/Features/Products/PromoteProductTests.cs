namespace Sankore.Modules.Administration.Tests.Features.Products;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Products;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Sandbox lot 4, « Copier en production » for a product: replayed through the ordinary product
/// commands into the live tenant, matched by code, the CBS link left alone — and the same
/// container arrangement as the API, where a background scope outranks the request's token.
/// </summary>
public sealed class PromoteProductTests : IDisposable
{
    private readonly Guid _live = Guid.NewGuid();
    private readonly Guid _twin = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ServiceProvider _services;

    public PromoteProductTests()
    {
        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(Guid.NewGuid());
        me.TenantId.Returns(_twin);
        me.TeamTenantId.Returns(_live);
        me.DisplayName.Returns("Awa Koné");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(AdministrationDbContext).Assembly));
        services.AddScoped<ICurrentUser>(_ => BackgroundJobContext.CurrentUser ?? me);
        services.AddScoped<ITenantContext>(_ => BackgroundJobContext.CurrentTenant ?? new FixedTenantContext(_twin));
        services.AddScoped(sp => new AdministrationDbContext(
            new DbContextOptionsBuilder<AdministrationDbContext>().UseInMemoryDatabase(_database).Options,
            sp.GetRequiredService<ITenantContext>()));
        services.AddSingleton<ProductCatalogCache>();
        services.AddSingleton<ITenantModules>(FixedTenantModules.All);
        services.AddScoped<ProductionReplay>();
        services.AddScoped<ProductCopier>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private async Task<Guid> SeedAsync(Guid tenantId, string name, string? cbsId = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdministrationDbContext>();
        var product = ProductSpeciality.Create(tenantId, name, "CC", ProductCategory.Loan);
        if (cbsId is not null) product.LinkToCbs("T24", cbsId);
        db.ProductSpecialities.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<Result<PromotionResult>> PromoteAsync(Guid id, bool dryRun = false)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PromoteProductCommand(id, dryRun));
    }

    private async Task<List<ProductSpeciality>> LiveProductsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AdministrationDbContext>().ProductSpecialities
            .IgnoreQueryFilters().Where(p => p.TenantId == _live).ToListAsync();
    }

    [Fact]
    public async Task A_product_new_to_production_is_created_there_and_the_test_one_stays()
    {
        var test = await SeedAsync(_twin, "Crédit campagne");

        (await PromoteAsync(test, dryRun: true)).Value.Action.Should().Be(PromotionActions.Created);
        (await LiveProductsAsync()).Should().BeEmpty("a dry run writes nothing");

        var result = await PromoteAsync(test);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Action.Should().Be(PromotionActions.Created);
        var live = (await LiveProductsAsync()).Should().ContainSingle().Subject;
        live.Id.Should().Be(result.Value.ProductionId!.Value).And.NotBe(test);
        live.Name.Should().Be("Crédit campagne");
    }

    [Fact]
    public async Task The_production_product_of_the_same_code_is_updated_and_keeps_its_cbs_link()
    {
        var liveId = await SeedAsync(_live, "Ancien nom", cbsId: "T24-001");
        var test = await SeedAsync(_twin, "Crédit campagne 2027");

        var result = await PromoteAsync(test);

        result.Value.Action.Should().Be(PromotionActions.Updated);
        result.Value.ProductionId.Should().Be(liveId);
        result.Value.Notes.Should().ContainSingle(n => n.Contains("core banking"));
        var live = (await LiveProductsAsync()).Single();
        live.Name.Should().Be("Crédit campagne 2027");
        live.BusinessProductId.Should().Be("T24-001");
    }

    [Fact]
    public async Task Outside_test_mode_nothing_is_copied()
    {
        var test = await SeedAsync(_twin, "Crédit campagne");
        var me = Substitute.For<ICurrentUser>();
        me.TenantId.Returns(_live);
        me.TeamTenantId.Returns(_live);

        await using var scope = _services.CreateAsyncScope();
        var handler = new PromoteProductHandler(
            new ProductionReplay(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), me),
            scope.ServiceProvider.GetRequiredService<ProductCopier>());

        (await handler.Handle(new PromoteProductCommand(test, false), default)).Error.Should().Be(PromotionErrors.TestModeRequired);
    }
}
