namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.ProductRouting;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// BNF-B-16's preflight. What it reports is <c>ResolveInsurerForProductAsync</c>'s own decision,
/// so the cases below are that method's branches read back rather than performed: no connection,
/// a mapping, the single-connection fallback, and the refusal.
///
/// <para>
/// The reason it is worth a suite of its own is that the failure it describes is silent. A
/// subscription with no determinable insurer is refused inside the consumer, logged once, and the
/// holding sits in <c>PendingSync</c> for ever. If this report said "routable" where the
/// resolution says <c>MappingMissing</c>, it would be worse than nothing: an administrator would
/// read it as a clean bill.
/// </para>
/// </summary>
public sealed class ProductRoutingHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static readonly Guid HealthLine = Guid.NewGuid();
    private static readonly Guid LifeLine = Guid.NewGuid();

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private static IAdministrationModule Catalogue(params ProductSummary[] products)
    {
        var admin = Substitute.For<IAdministrationModule>();

        // The handler asks once per insurance category, so the double answers per category — a
        // single catch-all would hide a handler that asked for the wrong one.
        foreach (var category in new[] { "Insurance", "HealthInsurance", "ForecastInsurance" })
        {
            var slice = products.Where(p => p.Category == category).ToList();
            admin.ListProductsAsync(Tenant, category, Arg.Any<CancellationToken>())
                .Returns(slice);
        }

        return admin;
    }

    private static ProductSummary Line(
        Guid id, string code, string category = "Insurance", bool active = true)
        => new(code, $"Produit {code}", category, active, id);

    private async Task<ProductRoutingDto> RouteAsync(IAdministrationModule admin)
    {
        await using var db = _factory.CreateContext();

        var handler = new ProductRoutingHandler(db, admin, new FixedTenantContext(Tenant));
        var result = await handler.Handle(new ProductRoutingQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private IntegrationConnection SeedConnection(string name, bool active = true)
    {
        using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(
            seed, Tenant, IntegrationKind.Fake, name: name, active: active);
        seed.SaveChanges();
        return connection;
    }

    private void SeedProductMapping(Guid connectionId, Guid productLineId, string externalCode)
    {
        using var seed = _factory.CreateContext();
        seed.Mappings.Add(IntegrationMapping.Create(
            tenantId: Tenant,
            connectionId: connectionId,
            domain: MappingDomain.Product,
            // The product's ID, which is what the resolution compares. A mapping keyed on the
            // product CODE looks configured and resolves nothing.
            crmCode: productLineId.ToString(),
            externalCode: externalCode,
            createdBy: Guid.NewGuid(),
            clock: TimeProvider.System));
        seed.SaveChanges();
    }

    [Fact]
    public async Task With_no_insurance_connection_nothing_is_a_mapping_problem()
    {
        var routing = await RouteAsync(Catalogue(Line(HealthLine, "SANTE")));

        routing.ActiveInsuranceConnections.Should().Be(0);
        routing.Lines.Should().ContainSingle()
            .Which.Routability.Should().Be(ProductRoutability.NoInsuranceConnection);

        // Not counted as unroutable: the installation is incomplete, which is a different
        // conversation from a missing product mapping.
        routing.UnroutableCount.Should().Be(0);
    }

    [Fact]
    public async Task A_single_connection_routes_every_line_by_fallback_and_names_it()
    {
        var connection = SeedConnection("ORASS Vie");

        var routing = await RouteAsync(Catalogue(Line(HealthLine, "SANTE")));

        var line = routing.Lines.Should().ContainSingle().Subject;
        line.Routability.Should().Be(ProductRoutability.SingleConnectionFallback);
        line.ConnectionId.Should().Be(connection.Id);
        line.ConnectionName.Should().Be("ORASS Vie");
        routing.UnroutableCount.Should().Be(0);
    }

    [Fact]
    public async Task With_several_connections_an_unmapped_line_is_unroutable()
    {
        // The case that produces MappingMissing, and the whole reason for the endpoint: every
        // subscription of this line is refused today, and the only trace is a log line.
        SeedConnection("ORASS IARD");
        SeedConnection("ORASS Vie");

        var routing = await RouteAsync(Catalogue(Line(HealthLine, "SANTE")));

        routing.ActiveInsuranceConnections.Should().Be(2);
        routing.UnroutableCount.Should().Be(1);

        var line = routing.Lines.Should().ContainSingle().Subject;
        line.Routability.Should().Be(ProductRoutability.Unroutable);
        line.ConnectionId.Should().BeNull();
    }

    [Fact]
    public async Task A_mapping_wins_and_names_its_insurer()
    {
        var iard = SeedConnection("ORASS IARD");
        var vie = SeedConnection("ORASS Vie");

        SeedProductMapping(vie.Id, HealthLine, "ORASS-PROD-SANTE");

        var routing = await RouteAsync(Catalogue(
            Line(HealthLine, "SANTE"),
            Line(LifeLine, "VIE")));

        var health = routing.Lines.Single(l => l.ProductLineId == HealthLine);
        health.Routability.Should().Be(ProductRoutability.Mapped);
        health.ConnectionId.Should().Be(vie.Id);
        health.ConnectionName.Should().Be("ORASS Vie");

        // And the line nobody mapped is still reported as the fault it is — the point being that
        // one configured line does not make the institution configured.
        var life = routing.Lines.Single(l => l.ProductLineId == LifeLine);
        life.Routability.Should().Be(ProductRoutability.Unroutable);

        routing.UnroutableCount.Should().Be(1);
        iard.Should().NotBeNull();
    }

    [Fact]
    public async Task The_expected_crm_code_is_the_product_id_not_its_code()
    {
        // Exposed in the response because it is the value a CSV import must contain, and getting
        // it wrong produces a mapping row that resolves nothing while looking configured.
        SeedConnection("ORASS Vie");

        var routing = await RouteAsync(Catalogue(Line(HealthLine, "SANTE")));

        routing.Lines.Should().ContainSingle()
            .Which.CrmCode.Should().Be(HealthLine.ToString());
    }

    [Fact]
    public async Task A_withdrawn_line_is_not_reported()
    {
        // It takes no new subscription, so its missing mapping is not an operational fault — and
        // reporting it would bury the lines that are.
        SeedConnection("ORASS IARD");
        SeedConnection("ORASS Vie");

        var routing = await RouteAsync(Catalogue(Line(HealthLine, "SANTE", active: false)));

        routing.Lines.Should().BeEmpty();
        routing.UnroutableCount.Should().Be(0);
    }

    [Fact]
    public async Task An_inactive_connection_does_not_create_a_fallback()
    {
        // ResolveInsurerForProductAsync selects active connections only, so counting an inactive
        // one would report a fallback that will not happen.
        SeedConnection("ORASS Vie", active: false);

        var routing = await RouteAsync(Catalogue(Line(HealthLine, "SANTE")));

        routing.ActiveInsuranceConnections.Should().Be(0);
        routing.Lines.Should().ContainSingle()
            .Which.Routability.Should().Be(ProductRoutability.NoInsuranceConnection);
    }

    [Fact]
    public async Task All_three_insurance_categories_are_reported()
    {
        // Three and not one: a health cover and a prévoyance product are distributed under the
        // same kind of agreement, and asking only for Insurance would leave them invisible.
        SeedConnection("ORASS Vie");

        var routing = await RouteAsync(Catalogue(
            Line(Guid.NewGuid(), "IARD", "Insurance"),
            Line(Guid.NewGuid(), "SANTE", "HealthInsurance"),
            Line(Guid.NewGuid(), "PREV", "ForecastInsurance")));

        routing.Lines.Select(l => l.Code).Should().BeEquivalentTo(["IARD", "PREV", "SANTE"]);
    }

    [Fact]
    public async Task Another_tenants_mapping_does_not_route_this_tenants_line()
    {
        // The query filter does the isolation; this case is what proves it, because a mapping
        // leaking across tenants would report a line as configured and the subscription would
        // still be refused.
        SeedConnection("ORASS IARD");
        SeedConnection("ORASS Vie");

        var otherTenantId = Guid.NewGuid();

        await using (var other = _factory.ContextFor(otherTenantId))
        {
            var otherConnection = InsuranceTestHarness.SeedConnection(
                other, otherTenantId, IntegrationKind.Fake, name: "Autre assureur");

            other.Mappings.Add(IntegrationMapping.Create(
                tenantId: otherTenantId,
                connectionId: otherConnection.Id,
                domain: MappingDomain.Product,
                crmCode: HealthLine.ToString(),
                externalCode: "X",
                createdBy: Guid.NewGuid(),
                clock: TimeProvider.System));

            await other.SaveChangesAsync();
        }

        var routing = await RouteAsync(Catalogue(Line(HealthLine, "SANTE")));

        routing.Lines.Should().ContainSingle()
            .Which.Routability.Should().Be(ProductRoutability.Unroutable);
    }
}
