namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class WebhookIngestEndpointTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IPhoneBlindIndexer _indexer;

    public WebhookIngestEndpointTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _indexer = Substitute.For<IPhoneBlindIndexer>();
        _indexer.Compute(Arg.Any<string>()).Returns(ci => $"idx_{ci.Arg<string>()}");
    }

    public void Dispose() => _factory.Dispose();

    private async Task<LeadSourceConfig> SeedWebhookSource(ServerWebhookSettings? settings = null)
    {
        await using var db = _factory.CreateContext();
        var s = settings ?? new ServerWebhookSettings
        {
            FieldMappings =
            [
                new() { SourceField = "$.phone", TargetField = "phoneNumber" },
                new() { SourceField = "$.name", TargetField = "fullName" }
            ],
            ExternalIdPath = "$.id"
        };
        var source = LeadSourceConfig.Create(
            _tenantId, "HOOK", "Webhook",
            LeadChannelType.InboundWebhook, 0,
            IntegrationMode.ServerWebhook, settings: s);
        source.StartTesting();
        source.Activate();
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    [Fact]
    public async Task Single_item_ingested_successfully()
    {
        var source = await SeedWebhookSource();

        var payload = """{"id":"ext-1","phone":"+225070707","name":"Amadou"}""";

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db, _indexer, TimeProvider.System);
        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, payload, ExternalId: "ext-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(LeadIngestionStatus.Accepted);
        result.Value.LeadId.Should().NotBeNull();
    }

    [Fact]
    public async Task Idempotent_resubmission_returns_existing()
    {
        var source = await SeedWebhookSource();
        var payload = """{"id":"ext-2","phone":"+225070707","name":"Test"}""";

        await using var db1 = _factory.CreateContext();
        var h1 = new IngestInboundLeadHandler(db1, _indexer, TimeProvider.System);
        var first = await h1.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, payload, ExternalId: "ext-2"), CancellationToken.None);

        await using var db2 = _factory.CreateContext();
        var h2 = new IngestInboundLeadHandler(db2, _indexer, TimeProvider.System);
        var second = await h2.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, payload, ExternalId: "ext-2"), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.IngestionId.Should().Be(first.Value.IngestionId);
    }

    [Fact]
    public async Task Paused_source_is_rejected()
    {
        await using var seedDb = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "PAUSED", "Paused",
            LeadChannelType.InboundWebhook, 0, IntegrationMode.ServerWebhook,
            settings: new ServerWebhookSettings
            {
                FieldMappings =
                [
                    new() { SourceField = "$.phone", TargetField = "phoneNumber" },
                    new() { SourceField = "$.name", TargetField = "fullName" }
                ]
            });
        source.StartTesting();
        source.Activate();
        source.Pause();
        seedDb.LeadSourceConfigs.Add(source);
        await seedDb.SaveChangesAsync();

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db, _indexer, TimeProvider.System);
        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, """{"phone":"0707","name":"t"}"""), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SOURCE_NOT_ACTIVE");
    }

    [Fact]
    public void ServerWebhookSettings_has_AllowedIpAddresses_and_ExternalIdPath()
    {
        var settings = new ServerWebhookSettings
        {
            AllowedIpAddresses = ["192.168.1.1", "10.0.0.1"],
            ExternalIdPath = "$.externalId",
            SignatureAlgorithm = "sha256",
            SignatureHeaderName = "X-Sankore-Signature"
        };

        settings.AllowedIpAddresses.Should().HaveCount(2);
        settings.ExternalIdPath.Should().Be("$.externalId");
    }
}
