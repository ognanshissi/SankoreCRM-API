namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using System.Text;
using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.Features.Mappings.ExportMappings;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

public sealed class ExportMappingsHandlerTests
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

    [Fact]
    public async Task Writes_the_header_and_the_rows_in_the_contract_order()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.IdDocType, "PASSPORT", "PASS", "Passeport"));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.IdDocType, "CNI", "ID_CARD", "Pièce d'identité"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ExportMappingsHandler(db).Handle(
            new ExportMappingsQuery(ConnectionId, MappingDomain.IdDocType), default);

        result.IsSuccess.Should().BeTrue();

        var text = Encoding.UTF8.GetString(result.Value.Content).TrimStart('﻿');
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

        lines[0].Should().Be("crm_code,external_code,label");
        lines[1].Should().StartWith("CNI,ID_CARD,");
        lines[2].Should().StartWith("PASSPORT,PASS,");
    }

    [Fact]
    public async Task Writes_a_byte_order_mark()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Country, "CI", "CIV", "Côte d'Ivoire"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ExportMappingsHandler(db).Handle(
            new ExportMappingsQuery(ConnectionId, MappingDomain.Country), default);

        // Without the BOM Excel reads "Côte d'Ivoire" as mojibake and the operator retypes the
        // whole table.
        result.Value.Content.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(result.Value.Content).Should().Contain("Côte d'Ivoire");
    }

    [Fact]
    public async Task An_empty_domain_exports_the_header_alone_as_a_template()
    {
        using var factory = await WithConnectionAsync();
        await using var db = factory.CreateContext();

        var result = await new ExportMappingsHandler(db).Handle(
            new ExportMappingsQuery(ConnectionId, MappingDomain.Sector), default);

        // Not a 404: this file is what an operator fills in to populate the table the first time.
        result.IsSuccess.Should().BeTrue();
        Encoding.UTF8.GetString(result.Value.Content).TrimStart('﻿').Trim()
            .Should().Be("crm_code,external_code,label");
    }

    [Fact]
    public async Task A_missing_label_exports_as_an_empty_cell()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Gender, "F", "FEMALE"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ExportMappingsHandler(db).Handle(
            new ExportMappingsQuery(ConnectionId, MappingDomain.Gender), default);

        Encoding.UTF8.GetString(result.Value.Content).Should().Contain("F,FEMALE,");
    }

    [Fact]
    public async Task The_exported_file_reads_back_through_the_import_reader()
    {
        using var factory = await WithConnectionAsync();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Profession, "COMMERCANTE",
                "TRADER", "Commerçante, marché"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var export = await new ExportMappingsHandler(db).Handle(
            new ExportMappingsQuery(ConnectionId, MappingDomain.Profession), default);

        var store = new MappingsMemoryFileStore();
        var reference = store.Seed(export.Value.Content, export.Value.FileName);

        var file = await new MappingImportReader(store).ReadAsync(reference, default);

        // The round trip is the point of sharing MappingCsvColumns: an export an operator cannot
        // correct and upload back is not an export, and the label here carries both a comma and
        // an accent.
        file.HasHeaderError.Should().BeFalse();
        var row = file.Rows.Single();
        row.CrmCode.Should().Be("COMMERCANTE");
        row.ExternalCode.Should().Be("TRADER");
        row.Label.Should().Be("Commerçante, marché");
    }

    [Fact]
    public async Task The_file_name_names_the_domain_and_the_connection()
    {
        using var factory = await WithConnectionAsync();
        await using var db = factory.CreateContext();

        var result = await new ExportMappingsHandler(db).Handle(
            new ExportMappingsQuery(ConnectionId, MappingDomain.MaritalStatus), default);

        result.Value.FileName.Should().Be(
            $"integration-mappings-maritalstatus-{ConnectionId:N}.csv");
        result.Value.ContentType.Should().Be("text/csv; charset=utf-8");
    }

    [Fact]
    public async Task Another_tenants_connection_is_not_found()
    {
        using var factory = await WithConnectionAsync();
        await using var db = factory.ContextFor(Guid.NewGuid());

        var result = await new ExportMappingsHandler(db).Handle(
            new ExportMappingsQuery(ConnectionId, MappingDomain.Country), default);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    [Fact]
    public async Task Another_tenants_rows_are_not_exported()
    {
        using var factory = await WithConnectionAsync();
        var otherTenant = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                otherTenant, ConnectionId, MappingDomain.Country, "CI", "LEAKED"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new ExportMappingsHandler(db).Handle(
            new ExportMappingsQuery(ConnectionId, MappingDomain.Country), default);

        Encoding.UTF8.GetString(result.Value.Content).Should().NotContain("LEAKED");
    }
}
