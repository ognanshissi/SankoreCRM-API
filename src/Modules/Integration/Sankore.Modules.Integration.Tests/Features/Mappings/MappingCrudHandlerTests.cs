namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings.DeleteMapping;
using Sankore.Modules.Integration.Features.Mappings.ListMappings;
using Sankore.Modules.Integration.Features.Mappings.UpsertMapping;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class MappingCrudHandlerTests
{
    private static readonly Guid ConnectionId = new("11111111-1111-1111-1111-111111111111");

    private static UpsertMappingHandler Upsert(
        IntegrationDbContext db, Guid tenantId, MappingsTestClock clock)
        => new(
            db,
            new FixedTenantContext(tenantId),
            new MappingsStubCurrentUser(MappingsTestFixtures.Actor),
            clock);

    private static async Task<TestIntegrationDbContextFactory> WithConnectionAsync()
    {
        var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());

        await using var seed = factory.CreateContext();
        seed.Connections.Add(MappingsTestFixtures.Connection(factory.TenantId, ConnectionId));
        await seed.SaveChangesAsync();

        return factory;
    }

    // ── Upsert ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_creates_a_mapping()
    {
        using var factory = await WithConnectionAsync();
        var clock = MappingsTestClock.At(2026, 10, 8);

        await using var db = factory.CreateContext();

        var result = await Upsert(db, factory.TenantId, clock).Handle(
            new UpsertMappingCommand(
                ConnectionId, MappingDomain.Product, "EPARGNE", "SAV001", "Épargne classique"),
            default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Created.Should().BeTrue();
        result.Value.Mapping.Domain.Should().Be("Product");
        result.Value.Mapping.CrmCode.Should().Be("EPARGNE");

        await using var read = factory.CreateContext();
        var row = await read.Mappings.SingleAsync();
        row.TenantId.Should().Be(factory.TenantId);
        row.CreatedBy.Should().Be(MappingsTestFixtures.Actor);
        row.CreatedAt.Should().Be(clock.Now);
    }

    [Fact]
    public async Task Upsert_of_the_same_crm_code_is_an_update_and_keeps_the_id()
    {
        using var factory = await WithConnectionAsync();
        var clock = MappingsTestClock.At(2026, 10, 8);

        Guid firstId;

        await using (var db = factory.CreateContext())
        {
            var first = await Upsert(db, factory.TenantId, clock).Handle(
                new UpsertMappingCommand(ConnectionId, MappingDomain.Product, "EPARGNE", "SAV001", null),
                default);

            firstId = first.Value.Mapping.Id;
        }

        clock.Advance(TimeSpan.FromHours(3));

        await using (var db = factory.CreateContext())
        {
            var second = await Upsert(db, factory.TenantId, clock).Handle(
                new UpsertMappingCommand(
                    ConnectionId, MappingDomain.Product, "EPARGNE", "SAV002", "Corrigé"),
                default);

            // A duplicate (connection, domain, crmCode) is an edit, not a conflict: the unique
            // index exists to keep ONE translation per CRM code, not to refuse the second edit.
            second.Value.Created.Should().BeFalse();
            second.Value.Mapping.Id.Should().Be(firstId);
            second.Value.Mapping.ExternalCode.Should().Be("SAV002");
            second.Value.Mapping.UpdatedAt.Should().Be(clock.Now);
        }

        await using var read = factory.CreateContext();
        (await read.Mappings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_allows_two_crm_codes_to_share_one_external_code()
    {
        using var factory = await WithConnectionAsync();
        var clock = MappingsTestClock.At(2026, 10, 8);

        await using (var db = factory.CreateContext())
        {
            await Upsert(db, factory.TenantId, clock).Handle(
                new UpsertMappingCommand(ConnectionId, MappingDomain.Product, "TONTINE_A", "SAV001", null),
                default);
        }

        await using (var db = factory.CreateContext())
        {
            var second = await Upsert(db, factory.TenantId, clock).Handle(
                new UpsertMappingCommand(ConnectionId, MappingDomain.Product, "TONTINE_B", "SAV001", null),
                default);

            // Two CRM products folding onto one CBS product is why the reverse index is not
            // unique; refusing it here would make that index a lie.
            second.IsSuccess.Should().BeTrue();
            second.Value.Created.Should().BeTrue();
        }

        await using var read = factory.CreateContext();
        (await read.Mappings.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Upsert_trims_the_crm_code_so_a_trailing_space_is_not_a_second_row()
    {
        using var factory = await WithConnectionAsync();
        var clock = MappingsTestClock.At(2026, 10, 8);

        await using (var db = factory.CreateContext())
        {
            await Upsert(db, factory.TenantId, clock).Handle(
                new UpsertMappingCommand(ConnectionId, MappingDomain.Country, "CI", "CIV", null), default);
        }

        await using (var db = factory.CreateContext())
        {
            var second = await Upsert(db, factory.TenantId, clock).Handle(
                new UpsertMappingCommand(ConnectionId, MappingDomain.Country, " CI ", "CIV2", null),
                default);

            second.Value.Created.Should().BeFalse();
        }

        await using var read = factory.CreateContext();
        (await read.Mappings.SingleAsync()).ExternalCode.Should().Be("CIV2");
    }

    [Fact]
    public async Task Upsert_refuses_an_unknown_connection()
    {
        using var factory = await WithConnectionAsync();
        await using var db = factory.CreateContext();

        var result = await Upsert(db, factory.TenantId, MappingsTestClock.At(2026, 10, 8)).Handle(
            new UpsertMappingCommand(Guid.NewGuid(), MappingDomain.Product, "EPARGNE", "SAV001", null),
            default);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    [Fact]
    public async Task Upsert_on_another_tenants_connection_is_not_found()
    {
        using var factory = await WithConnectionAsync();
        var otherTenant = Guid.NewGuid();

        await using var db = factory.ContextFor(otherTenant);

        var result = await Upsert(db, otherTenant, MappingsTestClock.At(2026, 10, 8)).Handle(
            new UpsertMappingCommand(ConnectionId, MappingDomain.Product, "EPARGNE", "SAV001", null),
            default);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_removes_the_row()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Gender, "F", "FEMALE"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new DeleteMappingHandler(db).Handle(
            new DeleteMappingCommand(ConnectionId, MappingDomain.Gender, "F"), default);

        result.IsSuccess.Should().BeTrue();

        await using var read = factory.CreateContext();
        (await read.Mappings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Delete_of_an_absent_mapping_is_not_found()
    {
        using var factory = await WithConnectionAsync();
        await using var db = factory.CreateContext();

        var result = await new DeleteMappingHandler(db).Handle(
            new DeleteMappingCommand(ConnectionId, MappingDomain.Gender, "F"), default);

        result.Error.Should().Be(IntegrationErrors.MappingNotFound);
    }

    [Fact]
    public async Task Delete_does_not_reach_another_tenants_mapping()
    {
        using var factory = await WithConnectionAsync();
        var otherTenant = Guid.NewGuid();

        // Its OWN connection id. A connection id is a global primary key, so two tenants cannot
        // share one — and the delete is then aimed at the other tenant's connection, which is the
        // only way to prove that the tenant filter is what hides the row rather than a mismatched
        // argument.
        var otherConnectionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        await using (var seed = factory.ContextFor(otherTenant))
        {
            seed.Connections.Add(MappingsTestFixtures.Connection(otherTenant, otherConnectionId));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                otherTenant, otherConnectionId, MappingDomain.Gender, "F", "FEMALE"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new DeleteMappingHandler(db).Handle(
            new DeleteMappingCommand(otherConnectionId, MappingDomain.Gender, "F"), default);

        // CONNECTION_NOT_FOUND and not MAPPING_NOT_FOUND: the handler checks the connection
        // first, and the other tenant's connection is already invisible, so the perimeter stops
        // the call one guard EARLIER than the mapping lookup. Either code would satisfy the
        // property that matters — "not found", never "forbidden", so nothing confirms that the
        // row exists somewhere — but asserting the one that actually fires is what would catch
        // the guard being removed.
        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);

        await using var read = factory.ContextFor(otherTenant);
        (await read.Mappings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_only_removes_the_named_domain()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Gender, "F", "FEMALE"));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Country, "F", "FRA"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        await new DeleteMappingHandler(db).Handle(
            new DeleteMappingCommand(ConnectionId, MappingDomain.Gender, "F"), default);

        await using var read = factory.CreateContext();
        (await read.Mappings.SingleAsync()).Domain.Should().Be(MappingDomain.Country);
    }

    // ── List ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_returns_every_domain_ordered_by_domain_then_code()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Product, "EPARGNE", "SAV001"));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.IdDocType, "PASSPORT", "PASS"));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.IdDocType, "CNI", "ID_CARD"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ListMappingsHandler(db).Handle(
            new ListMappingsQuery(ConnectionId, null), default);

        result.Value.TotalCount.Should().Be(3);
        result.Value.Items.Select(i => i.CrmCode).Should().Equal("CNI", "PASSPORT", "EPARGNE");
    }

    [Fact]
    public async Task List_filters_by_domain()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Product, "EPARGNE", "SAV001"));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.IdDocType, "CNI", "ID_CARD"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ListMappingsHandler(db).Handle(
            new ListMappingsQuery(ConnectionId, MappingDomain.Product), default);

        result.Value.Items.Should().ContainSingle().Which.CrmCode.Should().Be("EPARGNE");
    }

    [Fact]
    public async Task List_does_not_mix_two_connections_of_the_same_tenant()
    {
        using var factory = await WithConnectionAsync();
        var secondConnection = new Guid("22222222-2222-2222-2222-222222222222");

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(MappingsTestFixtures.Connection(factory.TenantId, secondConnection));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Product, "EPARGNE", "SAV001"));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, secondConnection, MappingDomain.Product, "EPARGNE", "PROD-X"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ListMappingsHandler(db).Handle(
            new ListMappingsQuery(secondConnection, null), default);

        // The whole reason the table is keyed per connection: an IMF wired to a CBS and to two
        // insurers has three vocabularies for the same CRM product.
        result.Value.Items.Should().ContainSingle().Which.ExternalCode.Should().Be("PROD-X");
    }

    [Fact]
    public async Task List_on_another_tenants_connection_is_not_found()
    {
        using var factory = await WithConnectionAsync();

        await using var db = factory.ContextFor(Guid.NewGuid());

        var result = await new ListMappingsHandler(db).Handle(
            new ListMappingsQuery(ConnectionId, null), default);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }
}
