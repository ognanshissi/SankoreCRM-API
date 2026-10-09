namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.Features.Mappings.ImportMappings;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class ImportMappingsHandlerTests
{
    private static readonly Guid ConnectionId = new("11111111-1111-1111-1111-111111111111");

    private static ImportMappingsHandler Handler(
        IntegrationDbContext db,
        MappingsMemoryFileStore store,
        Guid tenantId,
        MappingsTestClock? clock = null)
        => new(
            db,
            new MappingImportReader(store),
            new MappingImportValidator(),
            store,
            new FixedTenantContext(tenantId),
            new MappingsStubCurrentUser(MappingsTestFixtures.Actor),
            clock ?? MappingsTestClock.At(2026, 10, 8));

    private static async Task<TestIntegrationDbContextFactory> WithConnectionAsync()
    {
        var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());

        await using var seed = factory.CreateContext();
        seed.Connections.Add(MappingsTestFixtures.Connection(factory.TenantId, ConnectionId));
        await seed.SaveChangesAsync();

        return factory;
    }

    [Fact]
    public async Task Imports_the_valid_lines()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(
            "crm_code,external_code,label\nCNI,ID_CARD,Carte nationale\nPASSPORT,PASSPORT,Passeport\n");

        await using var db = factory.CreateContext();

        var result = await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        result.IsSuccess.Should().BeTrue();
        result.Value.ValidRows.Should().Be(2);

        await using var read = factory.CreateContext();
        var rows = await read.Mappings.OrderBy(m => m.CrmCode).ToListAsync();

        rows.Should().HaveCount(2);
        rows[0].CrmCode.Should().Be("CNI");
        rows[0].ExternalCode.Should().Be("ID_CARD");
        rows[0].Label.Should().Be("Carte nationale");
        rows[0].TenantId.Should().Be(factory.TenantId);
        rows[0].Domain.Should().Be(MappingDomain.IdDocType);
    }

    [Fact]
    public async Task Creates_nothing_for_a_line_that_fails_and_still_imports_the_others()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(
            "crm_code,external_code,label\nCNI,ID_CARD,\n,ORPHAN,\nPASSPORT,PASSPORT,\n");

        await using var db = factory.CreateContext();

        var result = await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        result.Value.TotalRows.Should().Be(3);
        result.Value.ValidRows.Should().Be(2);
        result.Value.InvalidRows.Should().Be(1);

        await using var read = factory.CreateContext();

        // The bad line created nothing — and refusing the whole file instead would make an
        // operator re-do four hundred good lines because of one.
        var codes = await read.Mappings.Select(m => m.CrmCode).ToListAsync();
        codes.Should().BeEquivalentTo(["CNI", "PASSPORT"]);
    }

    [Fact]
    public async Task An_in_file_collision_imports_neither_of_the_two_lines()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(
            "crm_code,external_code,label\nCNI,ID_CARD,\nCNI,NATIONAL_ID,\n");

        await using var db = factory.CreateContext();

        var result = await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        result.Value.ValidRows.Should().Be(0);

        await using var read = factory.CreateContext();
        (await read.Mappings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_code_that_already_has_a_mapping_is_updated_not_duplicated()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.IdDocType, "CNI", "OLD", "Ancien"));
            await seed.SaveChangesAsync();
        }

        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\nCNI,ID_CARD,Carte nationale\n");

        var clock = MappingsTestClock.At(2026, 10, 9);

        await using var db = factory.CreateContext();

        await Handler(db, store, factory.TenantId, clock).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        await using var read = factory.CreateContext();
        var row = await read.Mappings.SingleAsync();

        // Re-uploading a corrected file is how this table is maintained; a second row would
        // violate ux_integration_mapping_crm_code in production.
        row.ExternalCode.Should().Be("ID_CARD");
        row.Label.Should().Be("Carte nationale");
        row.UpdatedBy.Should().Be(MappingsTestFixtures.Actor);
        row.UpdatedAt.Should().Be(clock.Now);
    }

    [Fact]
    public async Task A_blank_label_is_stored_as_null_not_as_an_empty_string()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\nCNI,ID_CARD,\n");

        await using var db = factory.CreateContext();

        await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        await using var read = factory.CreateContext();
        (await read.Mappings.SingleAsync()).Label.Should().BeNull();
    }

    [Fact]
    public async Task The_import_only_touches_the_domain_it_was_given()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Country, "CNI", "UNRELATED"));
            await seed.SaveChangesAsync();
        }

        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\nCNI,ID_CARD,\n");

        await using var db = factory.CreateContext();

        await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        await using var read = factory.CreateContext();

        // The same CRM code lives in several domains — "CNI" as an id document type has nothing
        // to do with a country code that happens to read the same.
        (await read.Mappings.CountAsync()).Should().Be(2);
        (await read.Mappings.SingleAsync(m => m.Domain == MappingDomain.Country))
            .ExternalCode.Should().Be("UNRELATED");
    }

    [Fact]
    public async Task A_missing_header_column_fails_the_file_and_writes_nothing()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,label\nCNI,Carte\n");

        await using var db = factory.CreateContext();

        var result = await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("external_code");

        await using var read = factory.CreateContext();
        (await read.Mappings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_connection_is_not_found()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\nCNI,ID_CARD,\n");

        await using var db = factory.CreateContext();

        var result = await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(
                Guid.NewGuid(), MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    [Fact]
    public async Task Another_tenants_connection_is_not_found_rather_than_forbidden()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\nCNI,ID_CARD,\n");

        var otherTenant = Guid.NewGuid();
        await using var db = factory.ContextFor(otherTenant);

        var result = await Handler(db, store, otherTenant).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        // Not "forbidden": a 403 would confirm the id exists somewhere, which is enough to
        // enumerate another tenant's connections.
        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);

        await using var read = factory.CreateContext();
        (await read.Mappings.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_upload_is_deleted_on_the_success_path_when_asked(bool deleteAfterwards)
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,external_code,label\nCNI,ID_CARD,\n");

        await using var db = factory.CreateContext();

        await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(ConnectionId, MappingDomain.IdDocType, reference, deleteAfterwards),
            default);

        store.Deleted.Contains(reference).Should().Be(deleteAfterwards);
        store.Holds(reference).Should().Be(!deleteAfterwards);
    }

    [Fact]
    public async Task The_upload_is_deleted_on_the_failure_path_too()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed("crm_code,label\nCNI,Carte\n");

        await using var db = factory.CreateContext();

        var result = await Handler(db, store, factory.TenantId).Handle(
            new ImportMappingsCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        // The finally block is the point: a file left behind by a failed import is invisible, and
        // it holds a tenant's whole code table.
        result.IsFailure.Should().BeTrue();
        store.Deleted.Should().Contain(reference);
    }
}
