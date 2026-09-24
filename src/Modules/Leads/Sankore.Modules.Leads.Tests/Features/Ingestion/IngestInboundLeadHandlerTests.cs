namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

public sealed class IngestInboundLeadHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IPhoneBlindIndexer _indexer;

    public IngestInboundLeadHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _indexer = Substitute.For<IPhoneBlindIndexer>();
        _indexer.Compute(Arg.Any<string>()).Returns(ci => $"idx_{ci.Arg<string>()}");
    }

    public void Dispose() => _factory.Dispose();

    private async Task<LeadSourceConfig> SeedActiveSource(
        IntegrationMode mode = IntegrationMode.Internal,
        SourceSettings? settings = null,
        LeadChannelType channel = LeadChannelType.WalkIn)
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "SRC", "Source", channel, 0, mode, settings: settings);
        source.StartTesting();
        source.Activate();
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<LeadSourceConfig> SeedTestingSource()
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "TEST", "Testing", LeadChannelType.WalkIn, 0);
        source.StartTesting();
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private string SimplePayload(string phone = "+2250707070707", string name = "Amadou Diallo")
        => $$"""{"phoneNumber":"{{phone}}","fullName":"{{name}}"}""";

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Ingests_lead_and_creates_ingestion_record()
    {
        var source = await SeedActiveSource();

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db, _indexer, TimeProvider.System);
        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, SimplePayload()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(LeadIngestionStatus.Accepted);
        result.Value.LeadId.Should().NotBeNull();

        await using var verify = _factory.CreateContext();
        var ingestion = verify.LeadIngestions.Single(i => i.Id == result.Value.IngestionId);
        ingestion.Status.Should().Be(Domain.LeadIngestionStatus.Accepted);

        var lead = verify.Leads.Single(l => l.Id == result.Value.LeadId);
        lead.FullName.Should().Be("Amadou Diallo");
        lead.IsTest.Should().BeFalse();
    }

    // ── Idempotence ─────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_externalId_returns_existing_ingestion()
    {
        var source = await SeedActiveSource();

        await using var db1 = _factory.CreateContext();
        var handler1 = new IngestInboundLeadHandler(db1, _indexer, TimeProvider.System);
        var first = await handler1.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, SimplePayload(), ExternalId: "ext-001"), CancellationToken.None);

        await using var db2 = _factory.CreateContext();
        var handler2 = new IngestInboundLeadHandler(db2, _indexer, TimeProvider.System);
        var second = await handler2.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, SimplePayload(), ExternalId: "ext-001"), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.IngestionId.Should().Be(first.Value.IngestionId);
    }

    // ── Phone dedup ─────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_phone_within_dedup_window_marks_duplicate()
    {
        var source = await SeedActiveSource();

        // First ingestion
        await using var db1 = _factory.CreateContext();
        var handler1 = new IngestInboundLeadHandler(db1, _indexer, TimeProvider.System);
        await handler1.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, SimplePayload()), CancellationToken.None);

        // Second ingestion with same phone
        await using var db2 = _factory.CreateContext();
        var handler2 = new IngestInboundLeadHandler(db2, _indexer, TimeProvider.System);
        var result = await handler2.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, SimplePayload(name: "Other Name"), ExternalId: "ext-002"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(LeadIngestionStatus.Duplicate);
        result.Value.LeadId.Should().NotBeNull(); // points to existing lead
    }

    // ── Testing source → IsTest ─────────────────────────────────────────

    [Fact]
    public async Task Testing_source_creates_test_lead()
    {
        var source = await SeedTestingSource();

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db, _indexer, TimeProvider.System);
        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, SimplePayload()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var lead = verify.Leads.Single(l => l.Id == result.Value.LeadId);
        lead.IsTest.Should().BeTrue();
    }

    // ── Missing required fields → Failed ────────────────────────────────

    [Fact]
    public async Task Missing_required_fields_creates_failed_ingestion()
    {
        var source = await SeedActiveSource();

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db, _indexer, TimeProvider.System);
        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, """{"email":"test@test.com"}"""), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(LeadIngestionStatus.Failed);
        result.Value.LeadId.Should().BeNull();
        result.Value.Error.Should().Contain("phoneNumber");
    }

    // ── Invalid payload → Failed ────────────────────────────────────────

    [Fact]
    public async Task Invalid_json_payload_creates_failed_ingestion()
    {
        var source = await SeedActiveSource();

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db, _indexer, TimeProvider.System);
        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, "not valid json"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(LeadIngestionStatus.Failed);
        result.Value.Error.Should().Be("INVALID_PAYLOAD");
    }

    // ── Inactive source → rejected ──────────────────────────────────────

    [Fact]
    public async Task Paused_source_rejects_ingestion()
    {
        await using var seedDb = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "PAUSED", "Paused", LeadChannelType.WalkIn, 0);
        source.StartTesting();
        source.Activate();
        source.Pause();
        seedDb.LeadSourceConfigs.Add(source);
        await seedDb.SaveChangesAsync();

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db, _indexer, TimeProvider.System);
        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, SimplePayload()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SOURCE_NOT_ACTIVE");
    }

    // ── Field mapping mode ──────────────────────────────────────────────

    [Fact]
    public async Task Webhook_source_applies_field_mapping()
    {
        var settings = new ServerWebhookSettings
        {
            FieldMappings =
            [
                new() { SourceField = "$.contact.phone", TargetField = "phoneNumber", Transformation = FieldTransformation.Trim },
                new() { SourceField = "$.contact.name", TargetField = "fullName", Transformation = FieldTransformation.Trim },
                new() { SourceField = "$.contact.mail", TargetField = "email", Transformation = FieldTransformation.Trim }
            ]
        };
        var source = await SeedActiveSource(IntegrationMode.ServerWebhook, settings, LeadChannelType.InboundWebhook);

        var payload = """
        {
            "contact": {
                "phone": " +2250707070707 ",
                "name": " Test User ",
                "mail": "test@example.com"
            }
        }
        """;

        await using var db = _factory.CreateContext();
        var handler = new IngestInboundLeadHandler(db, _indexer, TimeProvider.System);
        var result = await handler.Handle(new IngestInboundLeadCommand(
            _tenantId, source.Id, payload), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(LeadIngestionStatus.Accepted);

        await using var verify = _factory.CreateContext();
        var lead = verify.Leads.Single(l => l.Id == result.Value.LeadId);
        lead.FullName.Should().Be("Test User");
        lead.PhoneNumber.Should().Be("+2250707070707");
        lead.Email.Should().Be("test@example.com");
    }
}
