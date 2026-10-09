namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings.ListUnmappedCodes;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Criterion 3 of INT-04. Two halves, tested apart: the set difference (here, against a catalog
/// double) and what the CRM's code lists are actually reachable as
/// (<see cref="ContractCrmCodeCatalogTests"/>).
/// </summary>
public sealed class ListUnmappedCodesHandlerTests
{
    private static readonly Guid ConnectionId = new("11111111-1111-1111-1111-111111111111");

    private static async Task<TestIntegrationDbContextFactory> WithConnectionAsync()
    {
        var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());

        await using var seed = factory.CreateContext();
        seed.Connections.Add(MappingsTestFixtures.Connection(factory.TenantId, ConnectionId));
        await seed.SaveChangesAsync();

        return factory;
    }

    private sealed class StubCatalog(CrmCodeList list) : ICrmCodeCatalog
    {
        public Task<CrmCodeList> GetAsync(Guid tenantId, MappingDomain domain, CancellationToken ct)
            => Task.FromResult(list);
    }

    private static CrmCodeList Complete(params string[] codes)
        => new(CrmCodeListAvailability.Complete, "test catalog",
            codes.Select(c => new CrmCode(c, $"Label {c}")).ToList());

    [Fact]
    public async Task Returns_the_crm_codes_that_have_no_mapping()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Agency, "AG-ABJ-01", "BR001"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ListUnmappedCodesHandler(
                db,
                new FixedTenantContext(factory.TenantId),
                new StubCatalog(Complete("AG-ABJ-01", "AG-BKE-02", "AG-YAM-03")))
            .Handle(new ListUnmappedCodesQuery(ConnectionId, MappingDomain.Agency), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Domain.Should().Be("Agency");
        result.Value.KnownCrmCodes.Should().Be(3);
        result.Value.MappedCodes.Should().Be(1);
        result.Value.Codes.Select(c => c.CrmCode).Should().Equal("AG-BKE-02", "AG-YAM-03");
        result.Value.Codes[0].Label.Should().Be("Label AG-BKE-02");
    }

    [Fact]
    public async Task A_fully_mapped_domain_returns_an_empty_list_marked_Complete()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Agency, "AG-ABJ-01", "BR001"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ListUnmappedCodesHandler(
                db,
                new FixedTenantContext(factory.TenantId),
                new StubCatalog(Complete("AG-ABJ-01")))
            .Handle(new ListUnmappedCodesQuery(ConnectionId, MappingDomain.Agency), default);

        result.Value.Codes.Should().BeEmpty();

        // The availability is what makes the emptiness meaningful: only Complete lets a screen
        // say "everything is mapped".
        result.Value.Availability.Should().Be("Complete");
    }

    [Fact]
    public async Task An_unavailable_list_is_reported_as_such_with_its_reason()
    {
        using var factory = await WithConnectionAsync();
        await using var db = factory.CreateContext();

        var result = await new ListUnmappedCodesHandler(
                db,
                new FixedTenantContext(factory.TenantId),
                new StubCatalog(CrmCodeList.Unavailable("No contract exposes this list.")))
            .Handle(new ListUnmappedCodesQuery(ConnectionId, MappingDomain.Profession), default);

        result.Value.Availability.Should().Be("Unavailable");
        result.Value.KnownCrmCodes.Should().Be(0);
        result.Value.Codes.Should().BeEmpty();

        // The sentence is returned verbatim: "nothing is missing" and "we cannot tell" look
        // identical in the payload otherwise, and they have opposite consequences for whoever is
        // about to switch the connection on.
        result.Value.Source.Should().Be("No contract exposes this list.");
    }

    [Fact]
    public async Task A_mapping_of_another_domain_does_not_count_as_mapped()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Country, "AG-ABJ-01", "XX"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ListUnmappedCodesHandler(
                db,
                new FixedTenantContext(factory.TenantId),
                new StubCatalog(Complete("AG-ABJ-01")))
            .Handle(new ListUnmappedCodesQuery(ConnectionId, MappingDomain.Agency), default);

        result.Value.MappedCodes.Should().Be(0);
        result.Value.Codes.Should().ContainSingle();
    }

    [Fact]
    public async Task A_mapping_on_another_connection_does_not_count_as_mapped()
    {
        using var factory = await WithConnectionAsync();
        var otherConnection = new Guid("22222222-2222-2222-2222-222222222222");

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(MappingsTestFixtures.Connection(factory.TenantId, otherConnection));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, otherConnection, MappingDomain.Agency, "AG-ABJ-01", "BR001"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ListUnmappedCodesHandler(
                db,
                new FixedTenantContext(factory.TenantId),
                new StubCatalog(Complete("AG-ABJ-01")))
            .Handle(new ListUnmappedCodesQuery(ConnectionId, MappingDomain.Agency), default);

        result.Value.Codes.Should().ContainSingle();
    }

    [Fact]
    public async Task A_mapping_differing_only_in_case_does_not_cover_the_code()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Agency, "ag-abj-01", "BR001"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ListUnmappedCodesHandler(
                db,
                new FixedTenantContext(factory.TenantId),
                new StubCatalog(Complete("AG-ABJ-01")))
            .Handle(new ListUnmappedCodesQuery(ConnectionId, MappingDomain.Agency), default);

        // Ordinal, matching the resolver and the unique index: hiding this code here would hide
        // exactly the dispatch that is going to fail with INTEGRATION_MAPPING_MISSING.
        result.Value.Codes.Should().ContainSingle().Which.CrmCode.Should().Be("AG-ABJ-01");
    }

    [Fact]
    public async Task Another_tenants_connection_is_not_found()
    {
        using var factory = await WithConnectionAsync();
        var otherTenant = Guid.NewGuid();

        await using var db = factory.ContextFor(otherTenant);

        var result = await new ListUnmappedCodesHandler(
                db, new FixedTenantContext(otherTenant), new StubCatalog(Complete("AG-ABJ-01")))
            .Handle(new ListUnmappedCodesQuery(ConnectionId, MappingDomain.Agency), default);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }
}

/// <summary>
/// What the CRM's own code lists are reachable as, through <c>*.PublicApi</c> contracts only.
/// These tests exist mostly to PIN the gaps: a domain quietly starting to answer a list that was
/// documented as unavailable is a change that has to be deliberate.
/// </summary>
public sealed class ContractCrmCodeCatalogTests
{
    private static readonly Guid TenantId = new("44444444-4444-4444-4444-444444444444");

    private static AgentSummary Agent(Guid agencyId) => new(
        Guid.NewGuid(), "AWA OUATTARA", agencyId, [], [], null, 0, 0, 0d, true);

    [Fact]
    public async Task Agency_is_sourced_from_the_available_agents_and_marked_Partial()
    {
        var agencyA = Guid.NewGuid();
        var agencyB = Guid.NewGuid();

        var administration = Substitute.For<IAdministrationModule>();
        administration.GetAvailableAgentsAsync(TenantId, null, Arg.Any<CancellationToken>())
            .Returns([Agent(agencyA), Agent(agencyA), Agent(agencyB)]);
        administration.GetAgencyAsync(TenantId, agencyA, Arg.Any<CancellationToken>())
            .Returns(new AgencySummary(agencyA, "AG-ABJ-01", "Abidjan Plateau", null, true));
        administration.GetAgencyAsync(TenantId, agencyB, Arg.Any<CancellationToken>())
            .Returns(new AgencySummary(agencyB, "AG-BKE-02", "Bouaké", agencyA, true));

        var list = await new ContractCrmCodeCatalog(administration)
            .GetAsync(TenantId, MappingDomain.Agency, default);

        list.Codes.Select(c => c.Code).Should().Equal("AG-ABJ-01", "AG-BKE-02");
        list.Codes[0].Label.Should().Be("Abidjan Plateau");

        // PARTIAL and never Complete: IAdministrationModule has no agency enumeration, so an
        // agency with no available commercial agent is invisible here. A screen that read this as
        // a complete list would tell an operator everything is mapped when it is not.
        list.Availability.Should().Be(CrmCodeListAvailability.Partial);
        list.Source.Should().Contain("PARTIAL");
    }

    [Fact]
    public async Task An_agency_that_no_longer_resolves_is_skipped()
    {
        var agencyId = Guid.NewGuid();

        var administration = Substitute.For<IAdministrationModule>();
        administration.GetAvailableAgentsAsync(TenantId, null, Arg.Any<CancellationToken>())
            .Returns([Agent(agencyId)]);
        administration.GetAgencyAsync(TenantId, agencyId, Arg.Any<CancellationToken>())
            .Returns((AgencySummary?)null);

        var list = await new ContractCrmCodeCatalog(administration)
            .GetAsync(TenantId, MappingDomain.Agency, default);

        // A dangling reference degrades to "skip", never to an assumption that it resolves.
        list.Codes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deactivated_agency_is_skipped()
    {
        var agencyId = Guid.NewGuid();

        var administration = Substitute.For<IAdministrationModule>();
        administration.GetAvailableAgentsAsync(TenantId, null, Arg.Any<CancellationToken>())
            .Returns([Agent(agencyId)]);
        administration.GetAgencyAsync(TenantId, agencyId, Arg.Any<CancellationToken>())
            .Returns(new AgencySummary(agencyId, "AG-OLD-09", "Ancienne agence", null, false));

        var list = await new ContractCrmCodeCatalog(administration)
            .GetAsync(TenantId, MappingDomain.Agency, default);

        list.Codes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MappingDomain.Product)]
    [InlineData(MappingDomain.IdDocType)]
    [InlineData(MappingDomain.Country)]
    [InlineData(MappingDomain.Gender)]
    [InlineData(MappingDomain.MaritalStatus)]
    [InlineData(MappingDomain.Profession)]
    [InlineData(MappingDomain.Sector)]
    public async Task Every_other_domain_is_explicitly_unavailable_and_says_why(MappingDomain domain)
    {
        var list = await new ContractCrmCodeCatalog(Substitute.For<IAdministrationModule>())
            .GetAsync(TenantId, domain, default);

        // Explicitly unavailable rather than silently empty, and with a sentence: no module
        // contract projects these lists today, and inventing one from ProductCategory or from
        // M01's internal enums would produce a plausible-looking wrong answer.
        list.Availability.Should().Be(CrmCodeListAvailability.Unavailable);
        list.Codes.Should().BeEmpty();
        list.Source.Should().Contain("Not available through a module contract");
    }

    [Fact]
    public async Task The_unavailable_reason_names_the_domain_it_is_about()
    {
        var list = await new ContractCrmCodeCatalog(Substitute.For<IAdministrationModule>())
            .GetAsync(TenantId, MappingDomain.Profession, default);

        list.Source.Should().Contain("Profession");
    }
}
