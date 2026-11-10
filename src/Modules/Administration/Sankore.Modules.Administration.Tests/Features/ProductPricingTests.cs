namespace Sankore.Modules.Administration.Tests.Features;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.CompanyInfo.PricingRules;
using Sankore.Modules.Administration.Features.Products;
using Sankore.Modules.Administration.Features.Products.SetProductPricing;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Quote lines, lot 1 (2026-11-10, product owner): a Standard product carries a price excluding
/// VAT, a unit and a VAT category; a module's product carries none; the institution owns the rates
/// (18 % and 9 % until it says otherwise) and the discount ceiling.
/// </summary>
public sealed class ProductPricingTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public ProductPricingTests() => _factory = new TestAdminDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<ProductSpeciality> SeedAsync(ProductCategory category)
    {
        var product = ProductSpeciality.Create(_tenant, "Ciment CPJ 45", "CIM-45", category);
        await using var db = _factory.CreateContext();
        db.ProductSpecialities.Add(product);
        await db.SaveChangesAsync(CancellationToken.None);
        return product;
    }

    private async Task<Result> PriceAsync(SetProductPricingCommand cmd)
    {
        await using var db = _factory.CreateContext();
        return await new SetProductPricingHandler(db, new ProductCatalogCache()).Handle(cmd, CancellationToken.None);
    }

    [Fact]
    public async Task A_standard_product_is_priced_and_reads_back()
    {
        var product = await SeedAsync(ProductCategory.Standard);

        var result = await PriceAsync(new(product.Id, 4_750m, "  sac  ", ProductVatCategory.Reduced));

        result.IsSuccess.Should().BeTrue();
        await using var db = _factory.CreateContext();
        var dto = ProductFormGate.ToDto((await db.ProductSpecialities.FindAsync([product.Id]))!);
        dto.UnitPrice.Should().Be(4_750m);
        dto.PriceUnit.Should().Be("sac");
        dto.VatCategory.Should().Be(ProductVatCategory.Reduced);
    }

    [Fact]
    public async Task A_new_product_is_unpriced_at_the_standard_rate()
    {
        var product = ProductSpeciality.Create(_tenant, "Prestation", "PRESTA", ProductCategory.Standard);

        product.UnitPrice.Should().BeNull();
        product.VatCategory.Should().Be(ProductVatCategory.Standard);
    }

    [Theory]
    [InlineData(ProductCategory.Loan)]
    [InlineData(ProductCategory.Insurance)]
    public async Task A_module_product_takes_no_price(ProductCategory category)
    {
        var product = await SeedAsync(category);

        var result = await PriceAsync(new(product.Id, 1_000m, "unité", ProductVatCategory.Standard));

        result.Error.Should().Be("PRODUCT_PRICING_STANDARD_ONLY");
    }

    [Fact]
    public async Task A_negative_price_is_refused_and_null_clears()
    {
        var product = await SeedAsync(ProductCategory.Standard);
        (await PriceAsync(new(product.Id, -1m, null, ProductVatCategory.Standard))).Error.Should().Be("PRODUCT_PRICE_INVALID");

        await PriceAsync(new(product.Id, 10m, "heure", ProductVatCategory.Exempt));
        (await PriceAsync(new(product.Id, null, " ", ProductVatCategory.Standard))).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var saved = (await db.ProductSpecialities.FindAsync([product.Id]))!;
        saved.UnitPrice.Should().BeNull();
        saved.PriceUnit.Should().BeNull();
        saved.VatCategory.Should().Be(ProductVatCategory.Standard);
    }

    [Fact]
    public async Task An_unknown_product_is_not_found()
    {
        (await PriceAsync(new(Guid.NewGuid(), 1m, null, ProductVatCategory.Standard))).Error.Should().Be("PRODUCT_NOT_FOUND");
    }

    [Fact]
    public async Task An_institution_without_identity_reads_the_ivorian_defaults()
    {
        await using var db = _factory.CreateContext();
        var rules = await new GetPricingRulesHandler(db).Handle(new GetPricingRulesQuery(_tenant), CancellationToken.None);

        rules.Should().Be(new PricingRulesDto(18m, 9m, null));
    }

    [Fact]
    public async Task The_institution_sets_its_rates_and_ceiling()
    {
        await using (var seed = _factory.CreateContext())
        {
            seed.CompanyInfos.Add(CompanyInfo.Create(_tenant, "Lagune Négoce", "Négoce"));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
            (await new SetPricingRulesHandler(db).Handle(new(19.25m, 10m, 15m), CancellationToken.None))
                .IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        (await new GetPricingRulesHandler(read).Handle(new GetPricingRulesQuery(_tenant), CancellationToken.None))
            .Should().Be(new PricingRulesDto(19.25m, 10m, 15m));
    }

    [Theory]
    [InlineData(18, 20, null, "VAT_RATES_INVALID")]
    [InlineData(101, 9, null, "VAT_RATES_INVALID")]
    [InlineData(18, -1, null, "VAT_RATES_INVALID")]
    [InlineData(18, 9, 120.0, "MAX_DISCOUNT_INVALID")]
    public void Rates_and_ceiling_are_checked(double standard, double reduced, double? ceiling, string error)
    {
        var info = CompanyInfo.Create(_tenant, "X", "Y");

        var result = info.SetPricingRules((decimal)standard, (decimal)reduced, (decimal?)ceiling);

        result.Error.Should().Be(error);
        info.VatStandardRate.Should().Be(18m);
        info.VatReducedRate.Should().Be(9m);
    }

    [Fact]
    public void A_zero_rate_and_no_ceiling_are_allowed()
    {
        var info = CompanyInfo.Create(_tenant, "X", "Y");

        info.SetPricingRules(0m, 0m, null).IsSuccess.Should().BeTrue();
        info.MaxDiscountPercent.Should().BeNull();
    }
}
