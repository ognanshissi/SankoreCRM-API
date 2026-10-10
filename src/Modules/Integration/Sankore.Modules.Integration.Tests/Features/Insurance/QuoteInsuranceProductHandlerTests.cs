namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.QuoteInsuranceProduct;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// ASS-03's second criterion: « Le tarif est soit fixe dans le catalogue, soit calculé par
/// l'assureur via IInsuranceProductPort si la capacité existe ».
///
/// <para>
/// Both branches behind one question, so the subscription screen never contains the branch.
/// </para>
/// </summary>
public sealed class QuoteInsuranceProductHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_catalogue_priced_product_answers_from_the_catalogue_with_no_insurer_call()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant, IntegrationKind.Fake);
        var product = InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id, fixedPremium: 7_500m, currency: "XOF");

        var adapter = new FakeAdapter();

        await using var db = _factory.CreateContext();
        var result = await new QuoteInsuranceProductHandler(
                db,
                InsuranceTestHarness.Resolver(db, adapter),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.Clock(),
                InsuranceTestHarness.Log<QuoteInsuranceProductHandler>())
            .Handle(
                new QuoteInsuranceProductQuery(product.Id, InsuranceTestHarness.Customer),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Source.Should().Be(QuoteSource.Catalogue);
        result.Value.PremiumAmount.Should().Be(7_500m);
        result.Value.Currency.Should().Be("XOF");

        // No insurer call AT ALL — asserted on the adapter's own call log rather than on the
        // amount, because the Fake adapter's default quote happens to be 7 500 too and an
        // amount-only assertion would pass even if the port had been called. A tenant that prices
        // its own products needs no capability, which is why ASS-03 declares the capability
        // rather than assuming it.
        adapter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_insurer_priced_product_is_quoted_through_the_port()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant, IntegrationKind.Fake);

        var product = InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id,
            insurerProductCode: FakeAdapter.SeededInsurerProductCode,
            pricingMode: ProductPricingMode.InsurerComputed,
            fixedPremium: null,
            currency: "XOF");

        var adapter = new FakeAdapter { QuotedPremium = 12_345m };

        await using var db = _factory.CreateContext();
        var result = await new QuoteInsuranceProductHandler(
                db,
                InsuranceTestHarness.Resolver(db, adapter),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.Clock(),
                InsuranceTestHarness.Log<QuoteInsuranceProductHandler>())
            .Handle(
                new QuoteInsuranceProductQuery(product.Id, InsuranceTestHarness.Customer),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Source.Should().Be(QuoteSource.Insurer);
        result.Value.PremiumAmount.Should().Be(12_345m);
    }

    [Fact]
    public async Task An_insurer_priced_product_whose_adapter_cannot_price_is_refused_by_capability()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant, IntegrationKind.Fake);

        var product = InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id,
            insurerProductCode: FakeAdapter.SeededInsurerProductCode,
            pricingMode: ProductPricingMode.InsurerComputed,
            fixedPremium: null);

        // A crippled fake: every capability EXCEPT pricing. The matrix is read from the adapter
        // precisely because the answer depends on the installation.
        var adapter = new FakeAdapter
        {
            Capabilities = new IntegrationCapabilities(
                Enum.GetValues<IntegrationCapability>()
                    .Where(c => c != IntegrationCapability.PriceProduct)
                    .ToDictionary(c => c, _ => CapabilityMode.RealTime)),
        };

        await using var db = _factory.CreateContext();
        var result = await new QuoteInsuranceProductHandler(
                db,
                InsuranceTestHarness.Resolver(db, adapter),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.Clock(),
                InsuranceTestHarness.Log<QuoteInsuranceProductHandler>())
            .Handle(
                new QuoteInsuranceProductQuery(product.Id, InsuranceTestHarness.Customer),
                CancellationToken.None);

        // The honest answer, not a defect: this insurer does not price through us, and the
        // product's configuration has to carry a fixed premium instead.
        result.Error.Should().StartWith(IntegrationErrors.CapabilityNotSupported);
    }

    [Fact]
    public async Task An_insurer_refusal_travels_out_under_the_adapters_own_code()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant, IntegrationKind.Fake);

        // A code the Fake adapter's catalogue does not hold: it answers a MAPPING failure rather
        // than a quote.
        var product = InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id,
            insurerProductCode: "ASS-INCONNU",
            pricingMode: ProductPricingMode.InsurerComputed,
            fixedPremium: null);

        await using var db = _factory.CreateContext();
        var result = await new QuoteInsuranceProductHandler(
                db,
                InsuranceTestHarness.Resolver(db, new FakeAdapter()),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.Clock(),
                InsuranceTestHarness.Log<QuoteInsuranceProductHandler>())
            .Handle(
                new QuoteInsuranceProductQuery(product.Id, InsuranceTestHarness.Customer),
                CancellationToken.None);

        result.IsFailure.Should().BeTrue();

        // The adapter's own code survives. Flattening every insurer failure into one local code
        // would lose the distinction between a transient outage (retry) and a refusal on the
        // merits (fix the configuration) — which is the entire purpose of ErrorFamily.
        result.Error.Should().StartWith(IntegrationErrors.MappingMissing);
    }

    [Fact]
    public async Task A_product_whose_insurer_connection_is_down_is_refused_BEFORE_any_insurer_call()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(
            seed, Tenant, IntegrationKind.Fake, active: false);

        var product = InsuranceTestHarness.SeedProduct(seed, Tenant, connection.Id);

        var adapter = new FakeAdapter();

        await using var db = _factory.CreateContext();
        var result = await new QuoteInsuranceProductHandler(
                db,
                InsuranceTestHarness.Resolver(db, adapter),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.Clock(),
                InsuranceTestHarness.Log<QuoteInsuranceProductHandler>())
            .Handle(
                new QuoteInsuranceProductQuery(product.Id, InsuranceTestHarness.Customer),
                CancellationToken.None);

        // ASS-03 criterion 4 applied to pricing: quoting a product that cannot be subscribed hands
        // the agent a price they would read out to the customer. The reason is named, so the
        // administrator knows which of the two rows to fix.
        result.Error.Should().StartWith(IntegrationErrors.InsuranceProductNotOfferable);
        result.Error.Should().Contain(ProductOfferability.ConnectionInactive);
    }

    [Fact]
    public async Task An_expired_product_is_refused_with_its_own_reason()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant, IntegrationKind.Fake);

        var product = InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id,
            effectiveFrom: new DateOnly(2025, 1, 1),
            effectiveTo: new DateOnly(2025, 12, 31));

        await using var db = _factory.CreateContext();
        var result = await new QuoteInsuranceProductHandler(
                db,
                InsuranceTestHarness.Resolver(db, new FakeAdapter()),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.Clock(),
                InsuranceTestHarness.Log<QuoteInsuranceProductHandler>())
            .Handle(
                new QuoteInsuranceProductQuery(product.Id, InsuranceTestHarness.Customer),
                CancellationToken.None);

        // The validity window goes stale by the passage of time alone — the second of the three
        // reasons a stored "offerable" flag would have been quietly wrong about.
        result.Error.Should().Contain(ProductOfferability.NoLongerEffective);
    }

    [Fact]
    public async Task An_unknown_product_answers_not_found()
    {
        await using var db = _factory.CreateContext();
        var result = await new QuoteInsuranceProductHandler(
                db,
                InsuranceTestHarness.Resolver(db),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.Clock(),
                InsuranceTestHarness.Log<QuoteInsuranceProductHandler>())
            .Handle(
                new QuoteInsuranceProductQuery(Guid.NewGuid(), InsuranceTestHarness.Customer),
                CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.InsuranceProductNotFound);
    }
}
