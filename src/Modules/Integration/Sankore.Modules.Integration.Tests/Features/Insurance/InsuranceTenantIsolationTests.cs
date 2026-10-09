namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.ActivateInsuranceProduct;
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
/// Cross-tenant isolation of the insurance family.
///
/// <para>
/// Tenant B's rows are written THROUGH a tenant-A context on purpose: EF's global query filters
/// apply to reads, not to inserts, so this seeds genuine foreign-tenant rows into the same
/// physical store — the situation a shared PostgreSQL database creates. Every assertion then
/// checks that tenant A cannot reach them.
/// </para>
///
/// <para>
/// What is at stake here is larger than in most zones. These tables hold an institution's whole
/// insurance portfolio, the premiums debited from its customers' accounts, its claim dossiers and
/// — in <c>ins_medical_questionnaire</c> — its customers' health answers. A missing query filter
/// would hand all of it to the next tenant on the same deployment.
/// </para>
///
/// <para>
/// Every route must answer <c>INSURANCE_PRODUCT_NOT_FOUND</c> and never 403: a 403 would confirm
/// that the id names a real catalogue entry, which says another institution is hosted here and
/// that it distributes insurance.
/// </para>
/// </summary>
public sealed class InsuranceTenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly TestIntegrationDbContextFactory _factory = new(TenantA);

    public void Dispose() => _factory.Dispose();

    private async Task<InsuranceProduct> SeedForeignProductAsync()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, TenantB, name: "ORASS de l'autre IMF");

        return InsuranceTestHarness.SeedProduct(seed, TenantB, connection.Id, "ASS-AUTRE");
    }

    [Fact]
    public async Task Another_tenants_product_is_invisible_to_the_detail_query()
    {
        var foreign = await SeedForeignProductAsync();

        await using var db = _factory.CreateContext();
        var result = await new GetInsuranceProductHandler(
                db, InsuranceTestHarness.Pricing(db), InsuranceTestHarness.Clock())
            .Handle(new GetInsuranceProductQuery(foreign.Id), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.InsuranceProductNotFound);
    }

    [Fact]
    public async Task Another_tenants_product_never_appears_in_the_list()
    {
        await SeedForeignProductAsync();

        await using var db = _factory.CreateContext();
        var result = await new ListInsuranceProductsHandler(
                db, InsuranceTestHarness.Pricing(db), InsuranceTestHarness.Clock())
            .Handle(new ListInsuranceProductsQuery(), CancellationToken.None);

        result.Value!.TotalCount.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_product_cannot_be_edited()
    {
        var foreign = await SeedForeignProductAsync();

        await using var db = _factory.CreateContext();
        var result = await new UpdateInsuranceProductHandler(
                db,
                InsuranceTestHarness.CreditCheck(TenantA),
                InsuranceTestHarness.Pricing(db),
                InsuranceTestHarness.User(TenantA),
                InsuranceTestHarness.Clock())
            .Handle(
                new UpdateInsuranceProductCommand(foreign.Id, InsuranceTestHarness.Body(name: "volé")),
                CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.InsuranceProductNotFound);

        await using var read = _factory.ContextFor(TenantB);
        (await read.InsuranceProducts.SingleAsync(p => p.Id == foreign.Id))
            .Name.Should().NotBe("volé");
    }

    [Fact]
    public async Task Another_tenants_product_cannot_be_activated_or_withdrawn()
    {
        var foreign = await SeedForeignProductAsync();

        await using var on = _factory.CreateContext();
        (await new ActivateInsuranceProductHandler(
                    on, InsuranceTestHarness.Pricing(on),
                    InsuranceTestHarness.User(TenantA), InsuranceTestHarness.Clock())
                .Handle(new ActivateInsuranceProductCommand(foreign.Id), CancellationToken.None))
            .Error.Should().Be(IntegrationErrors.InsuranceProductNotFound);

        await using var off = _factory.CreateContext();
        (await new DeactivateInsuranceProductHandler(
                    off, InsuranceTestHarness.Pricing(off),
                    InsuranceTestHarness.User(TenantA), InsuranceTestHarness.Clock())
                .Handle(new DeactivateInsuranceProductCommand(foreign.Id), CancellationToken.None))
            .Error.Should().Be(IntegrationErrors.InsuranceProductNotFound);

        // Still active for its own tenant: the refusals above changed nothing.
        await using var read = _factory.ContextFor(TenantB);
        (await read.InsuranceProducts.SingleAsync(p => p.Id == foreign.Id)).IsActive.Should().BeTrue();
    }

    /// <summary>
    /// Every table of the family, through its DbSet, over a store that holds one tenant-B row in
    /// each.
    ///
    /// <para>
    /// Written as one test rather than eleven because the thing being proved is the COMPLETENESS
    /// of the query-filter list in <c>IntegrationDbContext</c>, and a per-table test would pass
    /// for ten tables while the eleventh leaked. The row-by-row seeding below is the only way to
    /// exercise the filter: an empty table cannot distinguish isolation from a broken query, which
    /// is why each set is also read back through a tenant-B context and asserted non-empty.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_table_of_the_insurance_family_leaks_a_foreign_tenants_rows()
    {
        var clock = InsuranceTestHarness.Clock();

        await using var seed = _factory.CreateContext();

        var connection = InsuranceTestHarness.SeedConnection(seed, TenantB, name: "ORASS étranger");
        var product = InsuranceTestHarness.SeedProduct(seed, TenantB, connection.Id, "ASS-ETR");

        var subscription = InsuranceSubscription.Create(
            TenantB, connection.Id, InsuranceTestHarness.Customer, product.Id,
            new DateOnly(2026, 6, 1), 7_500m, "XOF", InsuranceTestHarness.Actor, clock);

        var policy = PolicyRecord.Create(
            TenantB, connection.Id, InsuranceTestHarness.Customer, "POL-ETR-1", "N°1",
            "ASS-ETR", PolicyStatus.Issued, new DateOnly(2026, 6, 1), 7_500m, "XOF",
            PremiumPeriodicity.Annual, clock, productId: product.Id);

        var claim = ClaimRecord.Create(
            TenantB, connection.Id, policy.Id, InsuranceTestHarness.Customer,
            new DateOnly(2026, 6, 15), "Décès", InsuranceTestHarness.Actor, clock);

        var statement = InsurerStatement.Create(TenantB, connection.Id, "2026-06", "XOF", clock);

        seed.InsuranceSubscriptions.Add(subscription);
        seed.Policies.Add(policy);
        seed.Claims.Add(claim);
        seed.InsurerStatements.Add(statement);
        await seed.SaveChangesAsync();

        seed.ConsentProofs.Add(ConsentProof.Create(
            TenantB, subscription.Id, InsuranceTestHarness.Customer,
            ConsentChannel.OneTimeCode, "v1", InsuranceTestHarness.Now,
            InsuranceTestHarness.Actor, clock, evidenceEncrypted: "v1:nonce:tag:cipher"));

        seed.MedicalQuestionnaires.Add(MedicalQuestionnaire.Create(
            TenantB, subscription.Id, InsuranceTestHarness.Customer, "QM-VIE-01",
            "v1:nonce:tag:cipher", InsuranceTestHarness.Actor, clock));

        seed.PolicyCertificates.Add(PolicyCertificate.Create(
            TenantB, policy.Id, "attestation.pdf", "application/pdf", 1024,
            new string('a', 64), "b1.0123456789abcdef.0123456789abcdef0123456789abcdef", clock));

        seed.PremiumInstalments.Add(PremiumInstalment.Create(
            TenantB, policy.Id, 1, new DateOnly(2027, 6, 1), 7_500m, "XOF", clock));

        seed.ClaimDocuments.Add(ClaimDocument.Create(
            TenantB, claim.Id, "PoliceReport", "pv.pdf", "application/pdf", 2048,
            new string('b', 64), "b1.fedcba9876543210.fedcba9876543210fedcba9876543210",
            InsuranceTestHarness.Actor, clock));

        seed.InsurerStatementLines.Add(InsurerStatementLine.Create(
            TenantB, statement.Id, StatementLineType.Subscription, policy.Id,
            InsuranceTestHarness.Customer, new DateOnly(2026, 6, 1), 7_500m, "XOF",
            0.15m, "N°1", "ASS-ETR", clock));

        await seed.SaveChangesAsync();

        // Read back as tenant B first. Without this the emptiness below would also be satisfied by
        // a seeding that silently failed — the test would then prove nothing at all.
        await using var owner = _factory.ContextFor(TenantB);
        (await owner.InsuranceProducts.CountAsync()).Should().Be(1);
        (await owner.InsuranceSubscriptions.CountAsync()).Should().Be(1);
        (await owner.ConsentProofs.CountAsync()).Should().Be(1);
        (await owner.MedicalQuestionnaires.CountAsync()).Should().Be(1);
        (await owner.Policies.CountAsync()).Should().Be(1);
        (await owner.PolicyCertificates.CountAsync()).Should().Be(1);
        (await owner.PremiumInstalments.CountAsync()).Should().Be(1);
        (await owner.Claims.CountAsync()).Should().Be(1);
        (await owner.ClaimDocuments.CountAsync()).Should().Be(1);
        (await owner.InsurerStatements.CountAsync()).Should().Be(1);
        (await owner.InsurerStatementLines.CountAsync()).Should().Be(1);

        await using var other = _factory.CreateContext();
        (await other.InsuranceProducts.CountAsync()).Should().Be(0);
        (await other.InsuranceSubscriptions.CountAsync()).Should().Be(0);
        (await other.ConsentProofs.CountAsync()).Should().Be(0);
        (await other.MedicalQuestionnaires.CountAsync()).Should().Be(0);
        (await other.Policies.CountAsync()).Should().Be(0);
        (await other.PolicyCertificates.CountAsync()).Should().Be(0);
        (await other.PremiumInstalments.CountAsync()).Should().Be(0);
        (await other.Claims.CountAsync()).Should().Be(0);
        (await other.ClaimDocuments.CountAsync()).Should().Be(0);
        (await other.InsurerStatements.CountAsync()).Should().Be(0);
        (await other.InsurerStatementLines.CountAsync()).Should().Be(0);
    }
}
