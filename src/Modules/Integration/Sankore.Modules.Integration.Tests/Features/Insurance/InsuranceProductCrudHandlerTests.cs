namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.ActivateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.DeactivateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.GetInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.ListInsuranceProducts;
using Sankore.Modules.Integration.Features.Insurance.UpdateInsuranceProduct;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The product catalogue of ASS-03, through its handlers.
/// </summary>
public sealed class InsuranceProductCrudHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private CreateInsuranceProductHandler Create(IntegrationDbContext db)
        => new(
            db,
            InsuranceTestHarness.CreditCheck(Tenant),
            InsuranceTestHarness.CrmCatalogue(),
            InsuranceTestHarness.Pricing(db),
            new FixedTenantContext(Tenant),
            InsuranceTestHarness.User(Tenant),
            InsuranceTestHarness.Clock());

    private UpdateInsuranceProductHandler Update(IntegrationDbContext db)
        => new(
            db,
            InsuranceTestHarness.CreditCheck(Tenant),
            InsuranceTestHarness.CrmCatalogue(),
            InsuranceTestHarness.Pricing(db),
            InsuranceTestHarness.User(Tenant),
            InsuranceTestHarness.Clock());

    [Fact]
    public async Task A_created_product_carries_its_terms_and_is_NOT_active()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        await using var db = _factory.CreateContext();
        var result = await Create(db).Handle(
            new CreateInsuranceProductCommand(
                connection.Id, "ASS-VIE-EMP", InsuranceTestHarness.Body()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Inactive on creation, like a connection: a product is configured in several steps and
        // must not be sellable between the first save and the last. If this ever flips, a
        // half-configured product becomes offerable the instant it is saved.
        result.Value!.IsActive.Should().BeFalse();
        result.Value.IsOfferable.Should().BeFalse();
        result.Value.NotOfferableReasons.Should().Contain(ProductOfferability.ProductInactive);

        result.Value.CommissionRate.Should().Be(0.15m);
        result.Value.Guarantees.Should().ContainSingle(g => g.Code == "DC");

        await using var read = _factory.CreateContext();
        var stored = await read.InsuranceProducts.SingleAsync();
        stored.InsurerProductCode.Should().Be("ASS-VIE-EMP");
        stored.ConnectionId.Should().Be(connection.Id);
    }

    [Fact]
    public async Task A_product_on_a_core_banking_connection_is_refused()
    {
        await using var seed = _factory.CreateContext();

        // A CBS connection, seeded through the same harness.
        var cbs = InsuranceTestHarness.SeedConnection(
            seed, Tenant, IntegrationKind.Temenos, "CBS principal",
            family: IntegrationFamily.CoreBanking);

        await using var db = _factory.CreateContext();
        var result = await Create(db).Handle(
            new CreateInsuranceProductCommand(cbs.Id, "ASS-VIE-EMP", InsuranceTestHarness.Body()),
            CancellationToken.None);

        // The failure mode this prevents: a catalogue entry on a core-banking connection would
        // resolve a CBS adapter when ASS-04 came to subscribe it — the error would then surface at
        // the counter, not on the configuration screen.
        result.Error.Should().Be(IntegrationErrors.InsuranceConnectionWrongFamily);

        await using var read = _factory.CreateContext();
        (await read.InsuranceProducts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_connection_answers_not_found_and_never_a_distinct_code()
    {
        await using var db = _factory.CreateContext();
        var result = await Create(db).Handle(
            new CreateInsuranceProductCommand(
                Guid.NewGuid(), "ASS-VIE-EMP", InsuranceTestHarness.Body()),
            CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    [Fact]
    public async Task The_same_insurer_product_code_twice_on_one_connection_is_refused()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);
        InsuranceTestHarness.SeedProduct(seed, Tenant, connection.Id, "ASS-VIE-EMP");

        await using var db = _factory.CreateContext();
        var result = await Create(db).Handle(
            new CreateInsuranceProductCommand(
                connection.Id, "ASS-VIE-EMP", InsuranceTestHarness.Body()),
            CancellationToken.None);

        // The handler's friendly half of ux_ins_product_insurer_code. The InMemory provider
        // enforces no unique index, so what this pins is the read — the index is the other half
        // and only PostgreSQL can prove it.
        result.Error.Should().Be(IntegrationErrors.InsuranceProductAlreadyExists);
    }

    [Fact]
    public async Task The_same_code_at_a_DIFFERENT_insurer_is_allowed()
    {
        await using var seed = _factory.CreateContext();
        var first = InsuranceTestHarness.SeedConnection(seed, Tenant, name: "ORASS vie");
        var second = InsuranceTestHarness.SeedConnection(seed, Tenant, name: "ORASS iard");
        InsuranceTestHarness.SeedProduct(seed, Tenant, first.Id, "ASS-VIE-EMP");

        await using var db = _factory.CreateContext();
        var result = await Create(db).Handle(
            new CreateInsuranceProductCommand(
                second.Id, "ASS-VIE-EMP", InsuranceTestHarness.Body()),
            CancellationToken.None);

        // The uniqueness is per CONNECTION, not per tenant: two insurers legitimately use the same
        // product code, and a tenant-wide index would make the second insurer unconfigurable.
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_update_changes_the_terms_and_cannot_move_the_insurer_or_its_code()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);
        var product = InsuranceTestHarness.SeedProduct(seed, Tenant, connection.Id);

        await using var db = _factory.CreateContext();
        var result = await Update(db).Handle(
            new UpdateInsuranceProductCommand(
                product.Id,
                InsuranceTestHarness.Body(
                    name: "Assurance emprunteur — 2026", fixedPremium: 9_000m, commissionRate: 0.2m)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Assurance emprunteur — 2026");
        result.Value.FixedPremiumAmount.Should().Be(9_000m);
        result.Value.CommissionRate.Should().Be(0.2m);

        // Identity survives the edit. UpdateInsuranceProductCommand carries neither field, and
        // this is the assertion that fails if somebody adds them: changing either would re-point
        // every subscription, policy and statement line that names this product.
        result.Value.ConnectionId.Should().Be(connection.Id);
        result.Value.InsurerProductCode.Should().Be("ASS-VIE-EMP");

        // The activation flag is not an update field either — it has its own route and audit row.
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task An_update_of_an_unknown_product_answers_not_found()
    {
        await using var db = _factory.CreateContext();
        var result = await Update(db).Handle(
            new UpdateInsuranceProductCommand(Guid.NewGuid(), InsuranceTestHarness.Body()),
            CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.InsuranceProductNotFound);
    }

    [Fact]
    public async Task Activation_is_idempotent_and_deactivation_keeps_the_row()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);
        var product = InsuranceTestHarness.SeedProduct(seed, Tenant, connection.Id, active: false);

        await using var db = _factory.CreateContext();

        var first = await new ActivateInsuranceProductHandler(
                db, InsuranceTestHarness.Pricing(db),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.User(Tenant), InsuranceTestHarness.Clock())
            .Handle(new ActivateInsuranceProductCommand(product.Id), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value!.IsActive.Should().BeTrue();

        await using var again = _factory.CreateContext();
        var second = await new ActivateInsuranceProductHandler(
                again, InsuranceTestHarness.Pricing(again),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.User(Tenant), InsuranceTestHarness.Clock())
            .Handle(new ActivateInsuranceProductCommand(product.Id), CancellationToken.None);

        // Idempotent: a double-click is a success, not a conflict — the same call
        // ActivateConnectionHandler makes, so the button never looks like a rule firing.
        second.IsSuccess.Should().BeTrue();

        await using var off = _factory.CreateContext();
        var third = await new DeactivateInsuranceProductHandler(
                off, InsuranceTestHarness.Pricing(off),
                InsuranceTestHarness.CrmCatalogue(),
                InsuranceTestHarness.User(Tenant), InsuranceTestHarness.Clock())
            .Handle(new DeactivateInsuranceProductCommand(product.Id), CancellationToken.None);

        third.IsSuccess.Should().BeTrue();
        third.Value!.IsActive.Should().BeFalse();

        // Withdrawal is a STATUS, not a delete: subscriptions, policies, instalments and a year of
        // statement lines name this product. A test that only checked IsActive would pass with a
        // handler that deleted the row.
        await using var read = _factory.CreateContext();
        (await read.InsuranceProducts.CountAsync(p => p.Id == product.Id)).Should().Be(1);
    }

    [Fact]
    public async Task The_list_is_grouped_by_insurer_and_filters_by_connection_and_activity()
    {
        await using var seed = _factory.CreateContext();
        var vie = InsuranceTestHarness.SeedConnection(seed, Tenant, name: "A — ORASS vie");
        var iard = InsuranceTestHarness.SeedConnection(seed, Tenant, name: "B — ORASS iard");

        InsuranceTestHarness.SeedProduct(seed, Tenant, vie.Id, "ASS-VIE-EMP", "Emprunteur");
        InsuranceTestHarness.SeedProduct(seed, Tenant, vie.Id, "ASS-VIE-OBS", "Obsèques", active: false);
        InsuranceTestHarness.SeedProduct(seed, Tenant, iard.Id, "ASS-AUTO", "Auto");

        await using var db = _factory.CreateContext();
        var handler = new ListInsuranceProductsHandler(
            db, InsuranceTestHarness.Pricing(db), InsuranceTestHarness.CrmCatalogue(), InsuranceTestHarness.Clock());

        var all = await handler.Handle(new ListInsuranceProductsQuery(), CancellationToken.None);
        all.Value!.TotalCount.Should().Be(3);
        all.Value.Items.Select(i => i.ConnectionName).Should().BeInAscendingOrder();

        var perConnection = await handler.Handle(
            new ListInsuranceProductsQuery(ConnectionId: vie.Id), CancellationToken.None);
        perConnection.Value!.TotalCount.Should().Be(2);

        var activeOnly = await handler.Handle(
            new ListInsuranceProductsQuery(IsActive: true), CancellationToken.None);
        activeOnly.Value!.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task OfferableOnly_excludes_the_products_of_a_deactivated_insurer()
    {
        await using var seed = _factory.CreateContext();
        var live = InsuranceTestHarness.SeedConnection(seed, Tenant, name: "ORASS vie");
        var down = InsuranceTestHarness.SeedConnection(
            seed, Tenant, name: "ORASS iard", active: false);

        InsuranceTestHarness.SeedProduct(seed, Tenant, live.Id, "ASS-VIE-EMP");
        InsuranceTestHarness.SeedProduct(seed, Tenant, down.Id, "ASS-AUTO");

        await using var db = _factory.CreateContext();
        var handler = new ListInsuranceProductsHandler(
            db, InsuranceTestHarness.Pricing(db), InsuranceTestHarness.CrmCatalogue(), InsuranceTestHarness.Clock());

        var offerable = await handler.Handle(
            new ListInsuranceProductsQuery(OfferableOnly: true), CancellationToken.None);

        // ASS-03, criterion 4, pushed into SQL so the filter and the paging agree. The product of
        // the deactivated insurer was never touched — this is derived from the CONNECTION's state,
        // which is exactly what a stored flag would have missed.
        offerable.Value!.TotalCount.Should().Be(1);
        offerable.Value.Items.Should().ContainSingle(i => i.ConnectionId == live.Id);

        var everything = await handler.Handle(
            new ListInsuranceProductsQuery(), CancellationToken.None);

        everything.Value!.Items
            .Single(i => i.ConnectionId == down.Id)
            .NotOfferableReasons.Should().Contain(ProductOfferability.ConnectionInactive);
    }

    [Fact]
    public async Task A_product_detail_reports_every_reason_it_cannot_be_offered()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant, active: false);

        var product = InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id,
            active: false,
            pricingMode: ProductPricingMode.InsurerComputed,
            fixedPremium: null,
            currency: "XOF",
            effectiveFrom: new DateOnly(2027, 1, 1));

        await using var db = _factory.CreateContext();
        var result = await new GetInsuranceProductHandler(
                db, InsuranceTestHarness.Pricing(db), InsuranceTestHarness.CrmCatalogue(), InsuranceTestHarness.Clock())
            .Handle(new GetInsuranceProductQuery(product.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // All of them, not the first: an administrator who fixes one and finds the next loses an
        // afternoon. No adapter is registered in this probe, so the pricing reason is present too.
        result.Value!.NotOfferableReasons.Should().BeEquivalentTo(
        [
            ProductOfferability.ProductInactive,
            ProductOfferability.ConnectionInactive,
            ProductOfferability.NotYetEffective,
            ProductOfferability.InsurerPricingUnavailable,
        ]);
    }
}
