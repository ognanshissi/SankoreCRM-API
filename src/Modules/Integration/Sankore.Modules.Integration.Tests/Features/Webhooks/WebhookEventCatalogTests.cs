namespace Sankore.Modules.Integration.Tests.Features.Webhooks;

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Sankore.Modules.Integration.Features.Webhooks;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The catalogue is the contract partners code against. These tests pin its codes, keep personal
/// values and typed motives out of every payload, and fail when a lifecycle event is published
/// without anybody deciding what a webhook may say about it.
/// </summary>
public sealed class WebhookEventCatalogTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public void The_codes_are_the_published_ones()
        => WebhookEventCatalog.Codes.Should().Equal(
            "lead.created", "lead.assigned", "lead.status_changed", "lead.stage_changed", "lead.qualified",
            "lead.converted", "lead.closed", "lead.reopened", "lead.merged", "lead.consent_withdrawn",
            "lead.erased",
            "project.created", "project.stage_changed", "project.closed",
            "client.created", "client.updated", "client.activated", "client.suspended", "client.archived",
            "client.transferred", "client.merged", "client.segment_changed", "client.kyc_validated",
            "client.kyc_rejected", "client.anonymized");

    [Fact]
    public void A_code_is_lower_case_and_dotted_and_an_event_type_appears_once()
    {
        WebhookEventCatalog.All.Should().OnlyContain(d => Regex.IsMatch(d.Code, "^[a-z]+\\.[a-z_]+$"));
        WebhookEventCatalog.All.Select(d => d.EventType).Should().OnlyHaveUniqueItems();

        // One code may come from two events (client.updated) only if they agree on how it reads.
        foreach (var group in WebhookEventCatalog.All.GroupBy(d => d.Code).Where(g => g.Count() > 1))
        {
            group.Select(d => (d.Family, d.Label, d.Version)).Distinct().Should().ContainSingle(
                "{0} must read the same whichever event produced it", group.Key);
        }
    }

    [Fact]
    public void Every_lead_and_project_lifecycle_event_has_a_decided_payload()
    {
        // A new public event in Leads.PublicApi.Events is a new fact partners may want; it reaches
        // them only when somebody writes its projection here.
        var published = typeof(LeadCreatedEvent).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(LeadCreatedEvent).Namespace
                        && typeof(IIntegrationEvent).IsAssignableFrom(t) && !t.IsAbstract);

        WebhookEventCatalog.All.Select(d => d.EventType).Should().Contain(published);
    }

    [Fact]
    public void Every_sample_projects_to_serialisable_camel_case_data()
    {
        foreach (var definition in WebhookEventCatalog.All)
        {
            var sample = definition.Sample(Tenant);
            sample.Should().BeOfType(definition.EventType);
            WebhookEventCatalog.For(sample).Should().BeSameAs(definition);

            var data = definition.Data(sample);
            data.Keys.Should().OnlyContain(k => Regex.IsMatch(k, "^[a-z][A-Za-z]*$"), definition.Code);
            data.Should().NotContainKey("tenantId", "the subscription is the tenant's; repeating it is noise");

            var act = () => JsonSerializer.Serialize(data);
            act.Should().NotThrow(definition.Code);
        }
    }

    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("address")]
    [InlineData("birth")]
    [InlineData("number")]
    [InlineData("reason")]
    [InlineData("detail")]
    [InlineData("comment")]
    public void No_payload_carries_a_personal_or_free_text_key(string fragment)
    {
        foreach (var definition in WebhookEventCatalog.All)
        {
            definition.Data(definition.Sample(Tenant)).Keys
                .Should().NotContain(k => k.Contains(fragment, StringComparison.OrdinalIgnoreCase), definition.Code);
        }
    }

    [Fact]
    public void Values_the_event_carries_but_the_webhook_must_not_are_left_out()
    {
        // The client number is the secret the remote KYC journey checks a customer against; the
        // motives are typed by operators. Both are on the events and on no payload.
        string Json(string code) => string.Join("\n", WebhookEventCatalog.All
            .Where(d => d.Code == code)
            .Select(d => JsonSerializer.Serialize(d.Data(d.Sample(Tenant)))));

        Json("client.created").Should().NotContain("CLI-000000");
        Json("client.updated").Should().NotContain("Correction");
        Json("client.suspended").Should().NotContain("—");
        Json("client.kyc_rejected").Should().NotContain("—");
    }

    [Fact]
    public void An_event_outside_the_catalogue_is_not_a_webhook()
        => WebhookEventCatalog.For(new LeadPepExposureProbe(Tenant)).Should().BeNull();

    [Fact]
    public void Every_entry_is_socle_until_a_module_event_is_added()
        => WebhookEventCatalog.All.Should().OnlyContain(d => d.RequiredModule == null);

    private sealed record LeadPepExposureProbe(Guid TenantId) : IntegrationEventBase;
}
