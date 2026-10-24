namespace Sankore.Modules.Administration.Tests.Features.Products;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Products.SetBeneficiaryPolicy;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Le palier KYC « None » (projets, lot 3) : un produit Standard peut se vendre sans KYC, et un
/// produit d'une catégorie réglementée jamais, quoi que coche l'administrateur.
/// </summary>
public sealed class NoKycTierTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public NoKycTierTests() => _factory = new TestAdminDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void A_new_standard_product_starts_without_kyc_and_any_other_keeps_the_baseline()
    {
        ProductSpeciality.Create(_tenant, "Carte", "CARTE", ProductCategory.Standard)
            .BeneficiaryPolicy.RequiredKycTier.Should().Be(KycRequirement.None);
        ProductSpeciality.Create(_tenant, "Crédit", "CRED", ProductCategory.Loan)
            .BeneficiaryPolicy.RequiredKycTier.Should().BeNull();
    }

    [Theory]
    [InlineData(ProductCategory.Loan)]
    [InlineData(ProductCategory.CurrentAccount)]
    [InlineData(ProductCategory.HealthInsurance)]
    public async Task A_regulated_product_refuses_the_no_kyc_tier(ProductCategory category)
    {
        var product = await SeedAsync(category);

        var result = await Set(product.Id, KycRequirement.None);

        result.Error.Should().Be("BENEFICIARY_POLICY_KYC_NONE_STANDARD_ONLY");
    }

    [Fact]
    public async Task A_standard_product_may_go_without_kyc_or_require_it()
    {
        var product = await SeedAsync(ProductCategory.Standard);

        (await Set(product.Id, "Full")).IsSuccess.Should().BeTrue();
        (await Set(product.Id, KycRequirement.None)).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Full", true)]
    [InlineData("none", false)]
    public void Only_none_waives_kyc(string? tier, bool required)
        => KycRequirement.IsRequired(tier).Should().Be(required);

    private async Task<ProductSpeciality> SeedAsync(ProductCategory category)
    {
        var product = ProductSpeciality.Create(_tenant, "Produit", "P-" + category, category);
        await using var db = _factory.CreateContext();
        db.ProductSpecialities.Add(product);
        await db.SaveChangesAsync(CancellationToken.None);
        return product;
    }

    private async Task<Result> Set(Guid productId, string tier)
    {
        await using var db = _factory.CreateContext();
        return await new SetBeneficiaryPolicyHandler(db, new ProductCatalogCache()).Handle(
            new SetBeneficiaryPolicyCommand(ProductId: productId, AllowMultipleHoldings: true, RequiredKycTier: tier),
            CancellationToken.None);
    }
}
