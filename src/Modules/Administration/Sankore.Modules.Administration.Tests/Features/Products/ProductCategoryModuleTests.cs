namespace Sankore.Modules.Administration.Tests.Features.Products;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.EntityForms.GetEntityForm;
using Sankore.Modules.Administration.Features.Products.CreateProduct;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// A paid module closes the creation of products in its categories — and only the creation: the
/// catalogue already built keeps working, and Standard is open to every tenant.
/// </summary>
public sealed class ProductCategoryModuleTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _db;

    public ProductCategoryModuleTests() => _db = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    private async Task<Result<Guid>> CreateAsync(ProductCategory category, ITenantModules modules, string code)
    {
        await using var db = _db.CreateContext();
        return await new CreateProductHandler(db, new FixedTenantContext(_tenantId), new ProductCatalogCache(), modules)
            .Handle(new CreateProductCommand("Produit", code, category), default);
    }

    private async Task<IReadOnlyList<string>> OfferedCategoriesAsync(ITenantModules modules)
    {
        await using var db = _db.CreateContext();
        var form = (await new GetEntityFormHandler(db, new FixedTenantContext(_tenantId), modules)
            .Handle(new GetEntityFormQuery(FormEntity.Product), default)).Value;
        return [.. form.Fields.Single(f => f.Key == "category").Options!.Select(o => o.Value)];
    }

    [Fact]
    public async Task A_banking_product_is_refused_without_Banking_Microfinance_and_nothing_is_written()
    {
        var result = await CreateAsync(ProductCategory.Tontine, new FixedTenantModules(TenantModules.Insurance), "TONT");

        result.Error.Should().Be(TenantModuleErrors.CategoryDisabled);
        await using var db = _db.CreateContext();
        (await db.ProductSpecialities.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_Standard_product_is_created_by_a_tenant_without_any_module()
    {
        (await CreateAsync(ProductCategory.Standard, FixedTenantModules.None, "STD")).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_banking_product_is_created_with_the_module()
    {
        (await CreateAsync(ProductCategory.GroupCredit, new FixedTenantModules(TenantModules.BankingMicrofinance), "GRP"))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_product_form_offers_Standard_and_the_categories_of_the_modules_in_force()
    {
        var socle = await OfferedCategoriesAsync(FixedTenantModules.None);
        var banking = await OfferedCategoriesAsync(new FixedTenantModules(TenantModules.BankingMicrofinance));
        var all = await OfferedCategoriesAsync(FixedTenantModules.All);

        socle.Should().Equal(nameof(ProductCategory.Standard));
        banking.Should().BeEquivalentTo(
            ProductCategories.BankingMicrofinance.Select(c => c.ToString()).Append(nameof(ProductCategory.Standard)));
        all.Should().HaveCount(Enum.GetValues<ProductCategory>().Length);
    }

    [Fact]
    public async Task The_other_forms_are_not_touched()
    {
        await using var db = _db.CreateContext();
        var lead = (await new GetEntityFormHandler(db, new FixedTenantContext(_tenantId), FixedTenantModules.None)
            .Handle(new GetEntityFormQuery(FormEntity.Lead), default)).Value;

        lead.Should().BeEquivalentTo(
            Sankore.Modules.Administration.Domain.Forms.EntityFormResolver.Resolve(FormEntity.Lead, 0, null));
    }
}
