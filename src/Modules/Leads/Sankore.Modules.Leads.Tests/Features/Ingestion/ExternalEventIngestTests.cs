namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Ingestion.Webhook;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// A partner reports an event about a lead it sent (webhooks lot 6). Matched by its external id or,
/// among ITS leads only, by phone; recorded once per partner event id; announced to the Workflow
/// module as an external trigger through the outbox.
/// </summary>
public sealed class ExternalEventIngestTests : IDisposable
{
    private const string Phone = "+2250707070707";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ISecretsModule _vault = Substitute.For<ISecretsModule>();
    private readonly IPhoneBlindIndexer _indexer = Substitute.For<IPhoneBlindIndexer>();

    public ExternalEventIngestTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _indexer.Compute(Arg.Any<string>()).Returns(ci => $"idx_{ci.Arg<string>()}");
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_event_on_a_known_external_id_starts_the_external_workflows_of_its_type()
    {
        var (source, lead) = await SeedAsync();

        var result = await Push(source, """
            { "id": "evt-1", "type": "Appointment.Booked", "lead": { "externalId": "ext-9" },
              "data": { "agence": "Plateau", "montant": 150000, "premier": true } }
            """);

        result.StatusCode.Should().Be(202);
        result.Status.Should().Be("Matched");
        result.LeadId.Should().Be(lead.Id);

        var signal = await SignalAsync();
        signal.IsExternal.Should().BeTrue();
        signal.EntityType.Should().Be("Lead");
        signal.EntityId.Should().Be(lead.Id);
        signal.EventName.Should().Be("appointment.booked", "the type is lower-cased, as triggers are");
        signal.Context!["agence"].ToString().Should().Be("Plateau");
        signal.Context["source"].ToString().Should().Be(source.Code);
    }

    [Fact]
    public async Task The_same_partner_event_twice_starts_nothing_more()
    {
        var (source, _) = await SeedAsync();
        const string body = """{ "id": "evt-1", "type": "file.completed", "lead": { "externalId": "ext-9" } }""";

        (await Push(source, body)).Status.Should().Be("Matched");
        var again = await Push(source, body);

        again.StatusCode.Should().Be(200);
        again.Status.Should().Be("Duplicate");
        await using var db = _factory.CreateContext();
        (await db.OutboxMessages.CountAsync(m => m.EventType.Contains(nameof(WorkflowTriggerSignal)))).Should().Be(1);
    }

    [Fact]
    public async Task A_phone_matches_only_the_leads_this_source_brought_in()
    {
        var (source, lead) = await SeedAsync();
        await SeedLeadFromAnotherSourceAsync();

        var result = await Push(source, $$"""{ "id": "evt-2", "type": "call.done", "lead": { "phone": "{{Phone}}" } }""");

        result.LeadId.Should().Be(lead.Id);

        // A source that never sent this person cannot reach them by phone.
        var (stranger, _) = await SeedAsync(code: "OTHER", withLead: false);
        (await Push(stranger, $$"""{ "id": "evt-3", "type": "call.done", "lead": { "phone": "{{Phone}}" } }""")).Status.Should().Be("Unmatched");
    }

    [Fact]
    public async Task An_unknown_lead_is_kept_unmatched_and_starts_nothing()
    {
        var (source, _) = await SeedAsync();

        var result = await Push(source, """{ "id": "evt-4", "type": "file.completed", "lead": { "externalId": "nobody" } }""");

        result.Status.Should().Be("Unmatched");
        await using var db = _factory.CreateContext();
        (await db.LeadExternalEvents.SingleAsync(e => e.ExternalEventId == "evt-4")).LeadId.Should().BeNull();
        (await db.OutboxMessages.CountAsync(m => m.EventType.Contains(nameof(WorkflowTriggerSignal)))).Should().Be(0);
    }

    [Theory]
    [InlineData("""{ "type": "a.b", "lead": { "externalId": "x" } }""", "EVENT_ID_REQUIRED")]
    [InlineData("""{ "id": "1", "type": "Pas un type !", "lead": { "externalId": "x" } }""", "EVENT_TYPE_INVALID")]
    [InlineData("""{ "id": "1", "type": "a.b" }""", "LEAD_REFERENCE_REQUIRED")]
    [InlineData("""{ "id": "1", "type": "a.b", "lead": { "externalId": "x" }, "data": { "n": { "deep": 1 } } }""", "DATA_NOT_FLAT")]
    [InlineData("""{ "id": "1", "type": "a.b", "lead": { "externalId": "x" }, "data": [1] }""", "DATA_NOT_AN_OBJECT")]
    [InlineData("not json", "INVALID_JSON")]
    public async Task A_malformed_event_is_refused_with_its_reason(string body, string error)
    {
        var (source, _) = await SeedAsync();

        var result = await Push(source, body);

        result.StatusCode.Should().Be(400);
        result.Error.Should().Be(error);
    }

    [Fact]
    public async Task A_signed_source_refuses_an_unsigned_event()
    {
        var (source, _) = await SeedAsync();
        var key = new SecretKey(_tenantId, WebhookSigning.Scope, source.Id, WebhookSigning.CurrentName);
        _vault.GetValueAsync(key, Arg.Any<CancellationToken>()).Returns("a-secret");

        (await Push(source, """{ "id": "evt-5", "type": "a.b", "lead": { "externalId": "ext-9" } }""")).StatusCode.Should().Be(401);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task<(LeadSourceConfig Source, Lead Lead)> SeedAsync(string code = "HOOK", bool withLead = true)
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(_tenantId, code, "Partenaire", LeadChannelType.InboundWebhook, 0,
            IntegrationMode.ServerWebhook, settings: new ServerWebhookSettings { ExternalIdPath = "$.id" });
        source.StartTesting();
        source.Activate();
        db.LeadSourceConfigs.Add(source);

        Lead lead = null!;
        if (withLead)
        {
            lead = Capture(source.Id);
            db.Leads.Add(lead);
            db.LeadIngestions.Add(LeadIngestion.Create(_tenantId, lead.Id, source.Id, TimeProvider.System, externalId: "ext-9"));
        }

        await db.SaveChangesAsync();
        return (source, lead);
    }

    private async Task SeedLeadFromAnotherSourceAsync()
    {
        await using var db = _factory.CreateContext();
        db.Leads.Add(Capture(Guid.NewGuid()));
        await db.SaveChangesAsync();
    }

    private Lead Capture(Guid sourceId)
        => Lead.Capture(_tenantId, "Awa Koné", Phone, LeadSource.Web, "Crédit", "fr", new GeoPoint(5.3, -4.0), null,
            TimeProvider.System, phoneBlindIndex: $"idx_{Phone}", leadSourceConfigId: sourceId);

    private async Task<ExternalEventResult> Push(LeadSourceConfig source, string body)
    {
        await using var db = _factory.CreateContext();
        var gate = new WebhookSourceGate(db, _vault, TimeProvider.System, NullLogger<WebhookSourceGate>.Instance);
        var handler = new IngestExternalEventHandler(db, gate, _indexer, new OutboxEventPublisher<LeadsDbContext>(db), TimeProvider.System);
        return (await handler.Handle(new IngestExternalEventCommand(
            source.PublicKey!, body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "203.0.113.7"), default)).Value!;
    }

    private async Task<WorkflowTriggerSignal> SignalAsync()
    {
        await using var db = _factory.CreateContext();
        var row = await db.OutboxMessages.SingleAsync(m => m.EventType.Contains(nameof(WorkflowTriggerSignal)));
        return (WorkflowTriggerSignal)JsonSerializer.Deserialize(row.PayloadJson, Type.GetType(row.EventType)!, OutboxJson.Options)!;
    }
}
