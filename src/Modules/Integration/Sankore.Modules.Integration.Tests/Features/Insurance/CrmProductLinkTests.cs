namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.GetInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.ListInsuranceProducts;
using Sankore.Modules.Integration.Features.Insurance.QuoteInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.UpdateInsuranceProduct;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The link between an insurance distribution agreement (<c>ins_product</c>) and the CRM catalogue
/// entry it realises (M12's <c>ProductSpeciality</c>).
///
/// <para>
/// The two tables exist for a reason these tests pin twice over: one catalogue entry distributed at
/// two insurers is TWO agreements with two insurer codes, two premiums and two commission rates, so
/// merging them is not available; and the catalogue is the identity every other module keys on, so
/// leaving them unlinked makes an insurance product invisible to product-by-code reporting. Hence a
/// code, checked at write time, never a foreign key.
/// </para>
/// </summary>
public sealed class CrmProductLinkTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    // ── Write time ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_product_naming_an_insurance_catalogue_entry_keeps_its_code_upper_cased()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        await using var db = _factory.CreateContext();

        var result = await Create(db).Handle(
            new CreateInsuranceProductCommand(
                connection.Id,
                "ASS-VIE-EMP",
                // Typed lower-case on purpose: M12 stores and matches its codes upper-cased, so a
                // link written as typed would resolve on this request and dangle on every read.
                InsuranceTestHarness.Body(crmProductCode: "ass-vie")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CrmProductCode.Should().Be("ASS-VIE");

        await using var read = _factory.CreateContext();
        var stored = await read.InsuranceProducts.SingleAsync();
        stored.CrmProductCode.Should().Be("ASS-VIE");
    }

    [Fact]
    public async Task A_product_naming_an_unknown_catalogue_entry_is_not_created()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        await using var db = _factory.CreateContext();

        var result = await Create(db).Handle(
            new CreateInsuranceProductCommand(
                connection.Id, "ASS-VIE-EMP",
                InsuranceTestHarness.Body(crmProductCode: "NOPE-404")),
            CancellationToken.None);

        result.Error.Should().StartWith(IntegrationErrors.InsuranceCrmProductInvalid);

        // Refused BEFORE the write: a dangling link is what every reader afterwards has to degrade
        // around, so the one moment it can be prevented is this one.
        await using var read = _factory.CreateContext();
        (await read.InsuranceProducts.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("Loan")]
    [InlineData("Savings")]
    [InlineData("Tontine")]
    public async Task A_product_naming_a_non_insurance_catalogue_entry_is_not_created(string category)
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        await using var db = _factory.CreateContext();

        var administration = InsuranceTestHarness.Administration(crmCategory: category);

        var result = await Create(db, administration).Handle(
            new CreateInsuranceProductCommand(
                connection.Id, "ASS-VIE-EMP",
                InsuranceTestHarness.Body(crmProductCode: InsuranceTestHarness.CrmCode)),
            CancellationToken.None);

        // The category check, not just existence. An agreement pointed at a loan code would show
        // up in the CRM's loan reporting and book its commission against a product the institution
        // does not insure.
        result.Error.Should().StartWith(IntegrationErrors.InsuranceCrmProductInvalid);
        result.Error.Should().Contain(category);
    }

    [Theory]
    [InlineData("Insurance")]
    [InlineData("HealthInsurance")]
    [InlineData("ForecastInsurance")]
    public async Task All_three_insurance_categories_are_accepted(string category)
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        await using var db = _factory.CreateContext();

        var administration = InsuranceTestHarness.Administration(crmCategory: category);

        var result = await Create(db, administration).Handle(
            new CreateInsuranceProductCommand(
                connection.Id, "ASS-VIE-EMP",
                InsuranceTestHarness.Body(crmProductCode: InsuranceTestHarness.CrmCode)),
            CancellationToken.None);

        // ProductCategory carries three insurance categories, and accepting only the bare one
        // would refuse a health cover for having been described precisely.
        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task A_product_naming_no_catalogue_entry_is_still_created()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        await using var db = _factory.CreateContext();

        var result = await Create(db).Handle(
            new CreateInsuranceProductCommand(
                connection.Id, "ASS-VIE-EMP", InsuranceTestHarness.Body()),
            CancellationToken.None);

        // The link is nullable and that is not a formality: every product configured before the
        // column existed has none, and the column was added without making them unreadable.
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.CrmProductCode.Should().BeNull();
        result.Value.NotOfferableReasons.Should().NotContain(ProductOfferability.CrmProductUnknown);
    }

    [Fact]
    public async Task One_catalogue_entry_is_distributed_at_two_insurers_as_two_products()
    {
        await using var seed = _factory.CreateContext();
        var vie = InsuranceTestHarness.SeedConnection(seed, Tenant, name: "ORASS vie");
        var iard = InsuranceTestHarness.SeedConnection(
            seed, Tenant, name: "ORASS IARD", at: InsuranceTestHarness.Now.AddMinutes(1));

        await using var db = _factory.CreateContext();

        // The same CRM product, at two insurers, with two insurer codes and two premiums. This is
        // the case that forbids folding ins_product into M12's catalogue: ONE catalogue entry,
        // several agreements, each with its own commercial terms.
        foreach (var (connection, insurerCode, premium) in new[]
                 {
                     (vie, "ORA-VIE-01", 7_500m),
                     (iard, "ORA-IARD-77", 9_900m),
                 })
        {
            var created = await Create(db).Handle(
                new CreateInsuranceProductCommand(
                    connection.Id,
                    insurerCode,
                    InsuranceTestHarness.Body(
                        crmProductCode: InsuranceTestHarness.CrmCode, fixedPremium: premium)),
                CancellationToken.None);

            created.IsSuccess.Should().BeTrue(created.Error);
        }

        await using var read = _factory.CreateContext();

        var rows = await read.InsuranceProducts.ToListAsync();

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(p => p.CrmProductCode == InsuranceTestHarness.CrmCode);
        rows.Select(p => p.InsurerProductCode).Should().BeEquivalentTo(["ORA-VIE-01", "ORA-IARD-77"]);
        rows.Select(p => p.FixedPremiumAmount).Should().BeEquivalentTo([7_500m, 9_900m]);
    }

    [Fact]
    public async Task An_edit_may_attach_a_catalogue_entry_and_may_detach_it()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);
        var product = InsuranceTestHarness.SeedProduct(seed, Tenant, connection.Id);

        await using var attach = _factory.CreateContext();

        var attached = await Update(attach).Handle(
            new UpdateInsuranceProductCommand(
                product.Id,
                InsuranceTestHarness.Body(crmProductCode: InsuranceTestHarness.CrmCode)),
            CancellationToken.None);

        attached.IsSuccess.Should().BeTrue(attached.Error);
        attached.Value!.CrmProductCode.Should().Be(InsuranceTestHarness.CrmCode);

        await using var detach = _factory.CreateContext();

        var detached = await Update(detach).Handle(
            new UpdateInsuranceProductCommand(product.Id, InsuranceTestHarness.Body()),
            CancellationToken.None);

        // Detaching is an ordinary edit and not a special route: the agreement stays sellable, it
        // simply stops claiming a catalogue identity.
        detached.IsSuccess.Should().BeTrue(detached.Error);
        detached.Value!.CrmProductCode.Should().BeNull();
    }

    [Fact]
    public async Task An_edit_naming_a_non_insurance_catalogue_entry_changes_nothing()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);
        var product = InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id, name: "Assurance emprunteur");

        await using var db = _factory.CreateContext();

        var administration = InsuranceTestHarness.Administration(crmCategory: "Savings");

        var result = await Update(db, administration).Handle(
            new UpdateInsuranceProductCommand(
                product.Id,
                InsuranceTestHarness.Body(
                    name: "Renommé", crmProductCode: InsuranceTestHarness.CrmCode)),
            CancellationToken.None);

        result.Error.Should().StartWith(IntegrationErrors.InsuranceCrmProductInvalid);

        // Refused before the aggregate is touched, so the name in the same body is not saved
        // either: a 422 that half-applied its body is worse than one that applied none of it.
        await using var read = _factory.CreateContext();
        (await read.InsuranceProducts.SingleAsync()).Name.Should().Be("Assurance emprunteur");
    }

    // ── Read time ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_product_whose_catalogue_entry_was_retired_is_not_offerable()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id, crmProductCode: InsuranceTestHarness.CrmCode);

        await using var db = _factory.CreateContext();

        // M12 retires rather than deletes, so the code still resolves — to an inactive product.
        var administration = InsuranceTestHarness.Administration(crmActive: false);

        var result = await new GetInsuranceProductHandler(
                db,
                InsuranceTestHarness.Pricing(db),
                InsuranceTestHarness.CrmCatalogue(Tenant, administration),
                InsuranceTestHarness.Clock())
            .Handle(
                new GetInsuranceProductQuery(
                    (await db.InsuranceProducts.SingleAsync()).Id),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);

        // The institution stopped selling the product: new subscriptions stop with it, at every
        // insurer that distributes it, the instant the catalogue entry is retired. Derived and
        // never stored — a column here would be correct until that moment and wrong afterwards.
        result.Value!.IsOfferable.Should().BeFalse();
        result.Value.NotOfferableReasons.Should().Contain(ProductOfferability.CrmProductWithdrawn);
    }

    [Fact]
    public async Task A_product_whose_catalogue_entry_no_longer_resolves_says_so()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        // Written by hand or before the write-time check existed: the only ways to get here, since
        // the handlers refuse an unresolvable code and M12 never deletes one.
        InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id, crmProductCode: "GONE-001");

        await using var db = _factory.CreateContext();

        var result = await new GetInsuranceProductHandler(
                db,
                InsuranceTestHarness.Pricing(db),
                InsuranceTestHarness.CrmCatalogue(Tenant),
                InsuranceTestHarness.Clock())
            .Handle(
                new GetInsuranceProductQuery(
                    (await db.InsuranceProducts.SingleAsync()).Id),
                CancellationToken.None);

        result.Value!.NotOfferableReasons.Should().Contain(ProductOfferability.CrmProductUnknown);
    }

    [Fact]
    public async Task The_list_resolves_each_catalogue_code_once_however_many_rows_name_it()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        for (var i = 0; i < 4; i++)
            InsuranceTestHarness.SeedProduct(
                seed, Tenant, connection.Id,
                insurerProductCode: $"ORA-{i:00}",
                crmProductCode: InsuranceTestHarness.CrmCode);

        await using var db = _factory.CreateContext();

        var administration = InsuranceTestHarness.Administration();

        var result = await new ListInsuranceProductsHandler(
                db,
                InsuranceTestHarness.Pricing(db),
                InsuranceTestHarness.CrmCatalogue(Tenant, administration),
                InsuranceTestHarness.Clock())
            .Handle(new ListInsuranceProductsQuery(), CancellationToken.None);

        result.Value!.Items.Should().HaveCount(4);

        // ONE call for four rows. The prefetch is what keeps a catalogue page of fifty products
        // from making fifty cross-module calls, and it is the reason the DTO takes the verdict as
        // a parameter instead of looking it up itself.
        await administration.Received(1).GetProductAsync(
            Arg.Any<Guid>(), InsuranceTestHarness.CrmCode, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_retired_catalogue_entry_cannot_be_quoted()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        var product = InsuranceTestHarness.SeedProduct(
            seed, Tenant, connection.Id, crmProductCode: InsuranceTestHarness.CrmCode);

        await using var db = _factory.CreateContext();

        var administration = InsuranceTestHarness.Administration(crmActive: false);

        var result = await new QuoteInsuranceProductHandler(
                db,
                InsuranceTestHarness.Resolver(db),
                InsuranceTestHarness.CrmCatalogue(Tenant, administration),
                InsuranceTestHarness.Clock(),
                InsuranceTestHarness.Log<QuoteInsuranceProductHandler>())
            .Handle(
                new QuoteInsuranceProductQuery(product.Id, InsuranceTestHarness.Customer),
                CancellationToken.None);

        // The gate is the same function the catalogue read uses, so "offerable" cannot come to
        // mean two things. A price read out to a customer for a product the institution has
        // stopped selling is the outcome this prevents — and this product is CatalogueFixed, so
        // without the gate it would have quoted with no insurer call at all.
        result.Error.Should().StartWith(IntegrationErrors.InsuranceProductNotOfferable);
        result.Error.Should().Contain(ProductOfferability.CrmProductWithdrawn);
    }

    [Fact]
    public void A_code_that_was_never_prefetched_reads_as_not_linked()
    {
        var catalogue = InsuranceTestHarness.CrmCatalogue(Tenant);

        // Not an exception and not a blocking call: the alternatives are a synchronous
        // cross-module lookup inside a projection and a catalogue screen that 500s on one row.
        catalogue.StatusOf(InsuranceTestHarness.CrmCode)
            .Should().Be(CrmCatalogueStatus.NotLinked);
    }

    // ── Builders ───────────────────────────────────────────────────────────

    private static CreateInsuranceProductHandler Create(
        IntegrationDbContext db, IAdministrationModule? administration = null)
        => new(
            db,
            InsuranceTestHarness.CreditCheck(Tenant),
            InsuranceTestHarness.CrmCatalogue(Tenant, administration),
            InsuranceTestHarness.Pricing(db),
            new FixedTenantContext(Tenant),
            InsuranceTestHarness.User(Tenant),
            InsuranceTestHarness.Clock());

    private static UpdateInsuranceProductHandler Update(
        IntegrationDbContext db, IAdministrationModule? administration = null)
        => new(
            db,
            InsuranceTestHarness.CreditCheck(Tenant),
            InsuranceTestHarness.CrmCatalogue(Tenant, administration),
            InsuranceTestHarness.Pricing(db),
            InsuranceTestHarness.User(Tenant),
            InsuranceTestHarness.Clock());
}
