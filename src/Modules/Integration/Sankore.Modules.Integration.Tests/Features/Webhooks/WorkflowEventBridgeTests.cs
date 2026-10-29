namespace Sankore.Modules.Integration.Tests.Features.Webhooks;

using FluentAssertions;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Integration.Features.Webhooks;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The catalogue's events become workflow triggers named by their code, on the entity they are
/// about. Before, the CRM offered workflows a single event (LEAD_CAPTURED) and the editor's event
/// name was a free field nothing else matched.
/// </summary>
public sealed class WorkflowEventBridgeTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000b1");

    [Fact]
    public void The_kernel_list_and_the_webhook_catalogue_name_the_same_events()
        => PlatformEvents.All.Select(e => e.Code).Should().BeEquivalentTo(WebhookEventCatalog.Codes);

    [Fact]
    public void A_lead_event_starts_lead_circuits_with_its_payload_as_context()
    {
        var leadId = Guid.NewGuid();

        var signal = WorkflowEventBridgeConsumer.ToSignal(new LeadStatusChangedEvent(Tenant, leadId, "New", "Open"))!;

        signal.TenantId.Should().Be(Tenant);
        signal.EntityType.Should().Be("Lead");
        signal.EntityId.Should().Be(leadId);
        signal.EventName.Should().Be("lead.status_changed");
        signal.IsExternal.Should().BeFalse();
        signal.Context!["status"].Should().Be("Open");
        signal.Context["leadId"].Should().Be(leadId.ToString());
    }

    [Fact]
    public void A_project_event_starts_project_circuits_with_numbers_the_conditions_can_compare()
    {
        var projectId = Guid.NewGuid();

        var signal = WorkflowEventBridgeConsumer.ToSignal(
            new ProjectClosedEvent(Tenant, projectId, Guid.NewGuid(), null, "Won", 1_500_000m, "XOF"))!;

        signal.EntityType.Should().Be("Opportunity");
        signal.EntityId.Should().Be(projectId);
        signal.Context!["outcome"].Should().Be("Won");
        signal.Context["amount"].Should().Be(1_500_000d);
        signal.Context.Should().NotContainKey("clientId", "a null value is left out rather than compared");
    }

    [Fact]
    public void A_client_event_starts_client_circuits()
    {
        var clientId = Guid.NewGuid();

        var signal = WorkflowEventBridgeConsumer.ToSignal(new ClientUpdatedEvent(Tenant, clientId, ["profession", "employer"], Guid.NewGuid()))!;

        signal.EntityType.Should().Be("Account");
        signal.EntityId.Should().Be(clientId);
        signal.Context!["fields"].Should().Be("profession,employer");
    }

    [Fact]
    public void An_erasure_notice_starts_no_work()
    {
        WorkflowEventBridgeConsumer.ToSignal(new LeadErasedEvent(Tenant, Guid.NewGuid(), DateTimeOffset.UtcNow)).Should().BeNull();
        WorkflowEventBridgeConsumer.ToSignal(new ClientAnonymizedEvent(Tenant, Guid.NewGuid(), DateTimeOffset.UtcNow)).Should().BeNull();
    }

    [Fact]
    public void Every_other_catalogue_event_produces_a_signal_on_its_entity()
    {
        foreach (var definition in WebhookEventCatalog.All)
        {
            var platform = PlatformEvents.Get(definition.Code);
            var signal = WorkflowEventBridgeConsumer.ToSignal(definition.Sample(Tenant));

            if (!platform.StartsWorkflows) { signal.Should().BeNull(definition.Code); continue; }

            signal.Should().NotBeNull(definition.Code);
            signal!.EntityType.Should().Be(platform.WorkflowEntityType, definition.Code);
            signal.EntityId.Should().NotBe(Guid.Empty, definition.Code);
        }
    }
}
