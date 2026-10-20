namespace Sankore.Modules.Leads.Tests.Features.CustomFields;

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Import.Readers;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Custom values arriving from outside the screens — a web form, the SDK, a webhook, a file — and
/// filtered in the list (dynamic forms, lot 6).
/// </summary>
public sealed class CustomFieldsInboundAndListTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IPhoneBlindIndexer _indexer = Substitute.For<IPhoneBlindIndexer>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public CustomFieldsInboundAndListTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _indexer.Compute(Arg.Any<string>()).Returns(ci => $"idx_{ci.Arg<string>()}");

        var catalog = NativeFormCatalog.For(FormEntity.Lead);
        _admin.GetEntityFormAsync(_tenantId, FormEntity.Lead, Arg.Any<CancellationToken>())
            .Returns(new EntityFormDto(FormEntity.Lead, 1, catalog.Sections,
            [
                .. catalog.Fields,
                new FormField("segment", FormFieldType.SingleChoice, "Segment", 1, "request",
                    Options: [new FormOption("Commercant", "Commerçant")]),
                new FormField("vip", FormFieldType.Boolean, "VIP", 2, "request"),
            ]));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_web_form_field_named_custom_key_reaches_the_lead_and_a_bad_value_never_costs_the_prospect()
    {
        LeadSourceConfig source;
        await using (var seed = _factory.CreateContext())
        {
            source = LeadSourceConfig.Create(_tenantId, "WEB", "Site", LeadChannelType.WebForm, 0);
            source.StartTesting();
            source.Activate();
            seed.LeadSourceConfigs.Add(source);
            await seed.SaveChangesAsync();
        }

        await using var db = _factory.CreateContext();
        var result = await new IngestInboundLeadHandler(db, _indexer, TimeProvider.System, _admin,
                NullLogger<IngestInboundLeadHandler>.Instance)
            .Handle(new IngestInboundLeadCommand(_tenantId, source.Id,
                """{"phoneNumber":"+2250707070707","fullName":"Awa Koné","custom.segment":"commerçant","custom.vip":"peut-être"}"""),
                default);

        result.Value.LeadId.Should().NotBeNull("an unreadable optional value is dropped, the prospect is kept");

        await using var verify = _factory.CreateContext();
        var lead = await verify.Leads.SingleAsync();
        var values = CustomFieldValues.Read(lead.CustomFieldsJson)!;
        values["segment"].GetString().Should().Be("Commercant");
        values.ContainsKey("vip").Should().BeFalse();
    }

    [Theory]
    [InlineData("custom.segment", true)]
    [InlineData("custom.Segment", true)]
    [InlineData("custom.", false)]
    [InlineData("custom.mon-champ", false)]
    [InlineData("phoneNumber", true)]
    [InlineData("nowhere", false)]
    public void A_mapping_rule_may_target_a_custom_field(string target, bool valid)
    {
        FieldMappingEngine.IsValidTarget(target).Should().Be(valid);
    }

    [Fact]
    public async Task A_csv_import_reads_the_custom_columns_by_header()
    {
        var store = Substitute.For<IFileStore>();
        store.ReadAsync("leads.csv", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream>(new MemoryStream(
            Encoding.UTF8.GetBytes("FullName,PhoneNumber,custom.segment,Notes\nAwa Koné,0707070707,Commerçant,à rappeler\n"))));

        var rows = await new FileImportReader(store).ReadAsync("leads.csv", default);

        rows.Single().FullName.Should().Be("Awa Koné");
        rows.Single().CustomFields.Should().BeEquivalentTo(new Dictionary<string, string?> { ["custom.segment"] = "Commerçant" });
    }

    /// <summary>
    /// The filter is a jsonb containment, which EF InMemory cannot evaluate — so, like the dispatch
    /// pool queries, it is checked against the real provider without opening a connection.
    /// </summary>
    [Fact]
    public void A_custom_filter_translates_to_a_jsonb_containment()
    {
        var options = new DbContextOptionsBuilder<LeadsDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new LeadsDbContext(options, new FixedTenantContext(_tenantId), LeadsTestEncryptor.Instance);

        var document = """{"segment":"Commercant"}""";
        var sql = db.Leads
            .Where(l => l.CustomFieldsJson != null && EF.Functions.JsonContains(l.CustomFieldsJson, document))
            .ToQueryString();

        sql.Should().Contain("custom_fields @>");
    }
}
