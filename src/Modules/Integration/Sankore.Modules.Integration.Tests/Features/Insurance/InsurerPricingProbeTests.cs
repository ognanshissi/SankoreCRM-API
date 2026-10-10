namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.ListInsuranceProducts;
using Sankore.Modules.Integration.Features.Insurance.QuoteInsuranceProduct;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// <c>InsurerPricingProbe</c> asked about TWO active connections of the same kind — the shape that
/// could not be tested before <c>ICbsAdapter.CapabilitiesFor</c> took its connection.
///
/// <para>
/// <b>Why the shape is normal and not exotic.</b>
/// <c>ux_integration_connection_active_core_banking</c> permits exactly one active core-banking
/// connection per tenant, and ASS-01 deliberately places no such limit on the insurance family:
/// IARD and Vie are legally separate undertakings in the CIMA zone, so an IMF distributing both
/// holds two ACTIVE connections of one kind — resolved to ONE adapter instance, by kind — whose
/// matrices can legitimately differ.
/// </para>
///
/// <para>
/// <b>The defect these two facts pin.</b> An adapter whose matrix depends on the row used to be
/// asked for it through a parameterless property, so it re-discovered "the tenant's connection" by
/// kind and answered the oldest active row's matrix for every one of them. With ORASS Vie pricing
/// and ORASS IARD not, the IARD product was reported <c>isOfferable: true</c> and its quote then
/// failed at the counter with <c>CAPABILITY_NOT_SUPPORTED</c> — in front of a customer, and in the
/// one place <see cref="IntegrationCapabilities"/> exists to prevent it. Both tests below fail if
/// <c>CapabilitiesFor</c> goes back to answering from a single connection.
/// </para>
///
/// <para>
/// <b>On the kind.</b> The connections are <see cref="IntegrationKind.Fake"/> and not
/// <c>Orass</c>: <c>OrassCapabilityMatrix</c> declares no <c>PriceProduct</c> on either carrier —
/// the argument is on <c>OrassAdapter</c>, and nothing may name a pricing operation the ORASS
/// specification has not described — so a real ORASS pair could not express "one prices, the other
/// does not". <see cref="FakeAdapter.CapabilitiesByConnection"/> exists for exactly this, and the
/// names carry the real story.
/// </para>
/// </summary>
public sealed class InsurerPricingProbeTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Two_connections_of_the_same_kind_each_get_their_own_answer()
    {
        await using var seed = _factory.CreateContext();

        var pair = SeedBothBranches(seed);

        await using var db = _factory.CreateContext();

        var adapter = AdapterThatCannotPriceFor(pair.Iard);

        // ONE probe for both connections, deliberately: its cache is keyed by connection id, and
        // a cache keyed by anything coarser (the adapter, the kind) would make the second answer
        // the first one — the same mistake as the property this fix replaced, one layer up.
        var probe = InsuranceTestHarness.Pricing(db, adapter);

        probe.CanPrice(pair.Vie).Should().BeTrue("this undertaking's installation prices");
        probe.CanPrice(pair.Iard).Should().BeFalse("this one does not, and its product must say so");

        // And the verdict a screen reads, through the real catalogue path: isOfferable is the FULL
        // verdict, so it is where a per-connection capability either arrives or is lost.
        var listed = await new ListInsuranceProductsHandler(
                db, probe, InsuranceTestHarness.CrmCatalogue(), InsuranceTestHarness.Clock())
            .Handle(new ListInsuranceProductsQuery(), CancellationToken.None);

        listed.IsSuccess.Should().BeTrue();

        var vieRow = listed.Value!.Items.Single(i => i.ConnectionId == pair.Vie.Id);
        var iardRow = listed.Value.Items.Single(i => i.ConnectionId == pair.Iard.Id);

        vieRow.IsOfferable.Should().BeTrue();
        vieRow.NotOfferableReasons.Should().BeEmpty();

        iardRow.IsOfferable.Should().BeFalse();

        // That reason and no other: both connections are active, both products are active and in
        // their validity window, so the ONLY thing that can separate these two rows is the matrix
        // each connection answered with. A test that merely asserted "not offerable" would also
        // pass on a row refused for the wrong reason.
        iardRow.NotOfferableReasons.Should().BeEquivalentTo(
            [ProductOfferability.InsurerPricingUnavailable]);
    }

    [Fact]
    public async Task The_catalogue_verdict_and_the_counter_agree_per_connection()
    {
        await using var seed = _factory.CreateContext();

        var pair = SeedBothBranches(seed);

        await using var db = _factory.CreateContext();

        var adapter = AdapterThatCannotPriceFor(pair.Iard);

        var handler = new QuoteInsuranceProductHandler(
            db,
            InsuranceTestHarness.Resolver(db, adapter),
            InsuranceTestHarness.CrmCatalogue(),
            InsuranceTestHarness.Clock(),
            InsuranceTestHarness.Log<QuoteInsuranceProductHandler>());

        var vie = await handler.Handle(
            new QuoteInsuranceProductQuery(pair.VieProduct.Id, InsuranceTestHarness.Customer),
            CancellationToken.None);

        var iard = await handler.Handle(
            new QuoteInsuranceProductQuery(pair.IardProduct.Id, InsuranceTestHarness.Customer),
            CancellationToken.None);

        vie.IsSuccess.Should().BeTrue();
        vie.Value!.Source.Should().Be(QuoteSource.Insurer);

        // The other half of the defect, and the half a customer saw. The catalogue said offerable
        // and the counter said CAPABILITY_NOT_SUPPORTED, because the two read the same matrix
        // through the same adapter and neither was told which insurer it was about. They disagree
        // only if the capability check stops being per connection: this quote is refused BEFORE
        // the port is reached — ResolvePort reads ResolvedAdapter.Capabilities — which is why the
        // refusal is a capability code and not an insurer error.
        iard.IsFailure.Should().BeTrue();
        iard.Error.Should().StartWith(IntegrationErrors.CapabilityNotSupported);

        // Nothing was asked of the insurer for the branch that cannot price, and everything was
        // for the one that can. Asserted on the adapter's own log rather than on the error: a
        // refusal returned AFTER a call would read identically here, and would mean a premium had
        // been quoted by an installation that does not price.
        adapter.Calls.Should().ContainSingle(c => c.Operation == FakeAdapterOperations.Price)
            .Which.Target.Should().Be(VieProductCode);
    }

    private const string VieProductCode = FakeAdapter.SeededInsurerProductCode;

    private const string IardProductCode = "ASS-IARD-AUTO";

    /// <summary>
    /// The two undertakings of one insurer group, each ACTIVE, each with one insurer-priced
    /// product. Vie is seeded FIRST, so it is the row an adapter re-discovering "the tenant's
    /// connection" by kind would have bound itself to — which is what makes the IARD assertions
    /// the ones that break when the per-connection answer is lost.
    /// </summary>
    private BothBranches SeedBothBranches(IntegrationDbContext seed)
    {
        var vie = InsuranceTestHarness.SeedConnection(
            seed, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        var iard = InsuranceTestHarness.SeedConnection(
            seed, Tenant, IntegrationKind.Fake, name: "ORASS IARD");

        return new BothBranches(
            vie,
            iard,
            InsurerPricedProduct(seed, vie.Id, VieProductCode, "Prévoyance emprunteur"),
            InsurerPricedProduct(seed, iard.Id, IardProductCode, "Automobile"));
    }

    private static InsuranceProduct InsurerPricedProduct(
        IntegrationDbContext db, Guid connectionId, string insurerProductCode, string name)
        => InsuranceTestHarness.SeedProduct(
            db, Tenant, connectionId,
            insurerProductCode: insurerProductCode,
            name: name,
            // The pricing capability is only consulted for a product the INSURER prices — a
            // catalogue-fixed one needs no capability at all (ASS-03, criterion 2), and would
            // make both rows offerable whatever the matrices said.
            pricingMode: ProductPricingMode.InsurerComputed,
            fixedPremium: null,
            currency: "XOF");

    /// <summary>
    /// One adapter — the two connections are of one kind, so keyed DI hands back the same instance
    /// for both — which prices for every connection except <paramref name="branch"/>.
    ///
    /// <para>
    /// The default <c>Capabilities</c> is everything live, so the OTHER branch keeps it and the
    /// dictionary carries only what differs. Expressed in that direction on purpose: it is what
    /// makes the regression observable. An adapter that ignored its connection would hand the
    /// default to both rows, and the branch that cannot price would be reported offerable and
    /// quotable — exactly the production symptom.
    /// </para>
    /// </summary>
    private static FakeAdapter AdapterThatCannotPriceFor(IntegrationConnection branch)
    {
        var adapter = new FakeAdapter();

        adapter.CapabilitiesByConnection[branch.Id] = new IntegrationCapabilities(
            adapter.Capabilities.Modes
                .Where(e => e.Key != IntegrationCapability.PriceProduct)
                .ToDictionary(e => e.Key, e => e.Value));

        return adapter;
    }

    private sealed record BothBranches(
        IntegrationConnection Vie,
        IntegrationConnection Iard,
        InsuranceProduct VieProduct,
        InsuranceProduct IardProduct);
}
