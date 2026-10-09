namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.Features.Mappings.ImportMappings;
using Sankore.Modules.Integration.Features.Mappings.ValidateMappingImport;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class ValidateMappingImportHandlerTests
{
    private const string FileBody =
        "crm_code,external_code,label\nCNI,ID_CARD,Carte\n,ORPHAN,\nCNI,NATIONAL_ID,\n";

    private static readonly Guid ConnectionId = new("11111111-1111-1111-1111-111111111111");

    private static ValidateMappingImportHandler Handler(
        IntegrationDbContext db, MappingsMemoryFileStore store)
        => new(db, new MappingImportReader(store), new MappingImportValidator(), store);

    private static async Task<TestIntegrationDbContextFactory> WithConnectionAsync()
    {
        var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());

        await using var seed = factory.CreateContext();
        seed.Connections.Add(MappingsTestFixtures.Connection(factory.TenantId, ConnectionId));
        await seed.SaveChangesAsync();

        return factory;
    }

    [Fact]
    public async Task Reports_the_lines_and_writes_nothing()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(FileBody);

        await using var db = factory.CreateContext();

        var result = await Handler(db, store).Handle(
            new ValidateMappingImportCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalRows.Should().Be(3);
        result.Value.InvalidRows.Should().Be(3);

        await using var read = factory.CreateContext();
        (await read.Mappings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Produces_exactly_the_report_the_import_produces()
    {
        using var dryFactory = await WithConnectionAsync();
        using var realFactory = await WithConnectionAsync();

        var dryStore = new MappingsMemoryFileStore();
        var realStore = new MappingsMemoryFileStore();

        var dryReference = dryStore.Seed(FileBody);
        var realReference = realStore.Seed(FileBody);

        await using var dryDb = dryFactory.CreateContext();
        await using var realDb = realFactory.CreateContext();

        var dryRun = await Handler(dryDb, dryStore).Handle(
            new ValidateMappingImportCommand(
                ConnectionId, MappingDomain.IdDocType, dryReference, DeleteAfterwards: true),
            default);

        var real = await new ImportMappingsHandler(
                realDb,
                new MappingImportReader(realStore),
                new MappingImportValidator(),
                realStore,
                new FixedTenantContext(realFactory.TenantId),
                new MappingsStubCurrentUser(MappingsTestFixtures.Actor),
                MappingsTestClock.At(2026, 10, 8))
            .Handle(
                new ImportMappingsCommand(
                    ConnectionId, MappingDomain.IdDocType, realReference, DeleteAfterwards: true),
                default);

        // The dry run is only worth running if the two agree; they do because both go through
        // the same validator, which reads no database of its own.
        dryRun.Value.Should().BeEquivalentTo(real.Value);
    }

    [Fact]
    public async Task The_upload_is_deleted_even_though_nothing_was_imported()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(FileBody);

        await using var db = factory.CreateContext();

        await Handler(db, store).Handle(
            new ValidateMappingImportCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        // A dry run is not an import, so there is no reason to keep a copy of the file.
        store.Deleted.Should().Contain(reference);
        store.Holds(reference).Should().BeFalse();
    }

    [Fact]
    public async Task Another_tenants_connection_is_not_found()
    {
        using var factory = await WithConnectionAsync();
        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(FileBody);

        await using var db = factory.ContextFor(Guid.NewGuid());

        var result = await Handler(db, store).Handle(
            new ValidateMappingImportCommand(
                ConnectionId, MappingDomain.IdDocType, reference, DeleteAfterwards: true),
            default);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
        store.Deleted.Should().Contain(reference);
    }

    [Fact]
    public void Is_audited_like_a_mutation()
    {
        var command = new ValidateMappingImportCommand(
            ConnectionId, MappingDomain.IdDocType, "ref", DeleteAfterwards: true);

        // ICommand is what activates AuditBehavior. A dry run pulls a tenant's whole code table
        // into the system, so who ran it has to be on the record — M01's
        // ValidateClientImportCommand is the precedent.
        command.Should().BeAssignableTo<Sankore.Shared.Infrastructure.Behaviors.ICommand>();
        command.Should().BeAssignableTo<IResourceCommand>();
        command.ResourceType.Should().Be("IntegrationMapping");
        command.ResourceId.Should().Be(ConnectionId.ToString());
    }
}
