namespace Sankore.Shared.Infrastructure.Tests.Outbox;

using System.Text.Json;
using FluentAssertions;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Pins the round trip every integration event makes: the publisher serializes it into
/// <c>outbox_messages.payload_json</c>, and the processor deserializes it from a boxed
/// <see cref="Type"/> before handing it to the bus.
///
/// This is worth its own test because the failure mode was silent. The publisher wrote
/// camelCase, the processor read with case-sensitive defaults, and since integration events are
/// positional records System.Text.Json matches JSON properties to constructor PARAMETERS: none
/// matched, every parameter took its default, and a perfectly well-formed event reached its
/// consumer with null strings and empty Guids. Nothing threw until a consumer touched a string.
/// </summary>
public sealed class OutboxJsonTests
{
    /// <summary>Shaped like a real integration event: positional record over the base type.</summary>
    private sealed record SampleEvent(
        Guid TenantId,
        Guid ClientId,
        string ClientNumber,
        string ClientType,
        Guid? SourceLeadId,
        int Count,
        bool Flag) : IntegrationEventBase;

    private static object RoundTrip(SampleEvent original)
    {
        // Exactly what OutboxEventPublisher does.
        var payload = JsonSerializer.Serialize(original, OutboxJson.Options);

        // Exactly what OutboxProcessor does: the static type is lost, only a Type remains.
        return JsonSerializer.Deserialize(payload, typeof(SampleEvent), OutboxJson.Options)!;
    }

    [Fact]
    public void An_event_survives_the_outbox_round_trip_intact()
    {
        var leadId = Guid.NewGuid();
        var original = new SampleEvent(
            TenantId: Guid.NewGuid(),
            ClientId: Guid.NewGuid(),
            ClientNumber: "ABJ-PLT-2026-000042",
            ClientType: "Legal",
            SourceLeadId: leadId,
            Count: 7,
            Flag: true);

        var restored = RoundTrip(original).Should().BeOfType<SampleEvent>().Subject;

        restored.TenantId.Should().Be(original.TenantId);
        restored.ClientId.Should().Be(original.ClientId);
        restored.ClientNumber.Should().Be("ABJ-PLT-2026-000042");
        restored.ClientType.Should().Be("Legal");
        restored.SourceLeadId.Should().Be(leadId);
        restored.Count.Should().Be(7);
        restored.Flag.Should().BeTrue();
    }

    [Fact]
    public void No_property_comes_back_at_its_default()
    {
        // The original bug did not throw on deserialization — it produced a fully-formed object
        // whose every field was default. Asserting the absence of defaults is what catches it.
        var restored = (SampleEvent)RoundTrip(new SampleEvent(
            Guid.NewGuid(), Guid.NewGuid(), "N-1", "Individual", Guid.NewGuid(), 1, true));

        restored.TenantId.Should().NotBeEmpty();
        restored.ClientId.Should().NotBeEmpty();
        restored.ClientNumber.Should().NotBeNull();
        restored.ClientType.Should().NotBeNull();
        restored.SourceLeadId.Should().NotBeNull();
    }

    [Fact]
    public void A_payload_written_in_pascal_case_still_deserializes()
    {
        // Rows written before the naming policy settled, or by any future change to it, must not
        // become undeliverable messages sitting in the outbox forever.
        const string pascalCasePayload = """
            {"TenantId":"11111111-1111-1111-1111-111111111111",
             "ClientId":"22222222-2222-2222-2222-222222222222",
             "ClientNumber":"ABJ-PLT-2026-000001","ClientType":"Individual",
             "SourceLeadId":null,"Count":3,"Flag":false}
            """;

        var restored = (SampleEvent)JsonSerializer.Deserialize(
            pascalCasePayload, typeof(SampleEvent), OutboxJson.Options)!;

        restored.ClientType.Should().Be("Individual");
        restored.ClientNumber.Should().Be("ABJ-PLT-2026-000001");
        restored.Count.Should().Be(3);
    }

    [Fact]
    public void The_payload_is_written_in_camel_case()
    {
        var payload = JsonSerializer.Serialize(
            new SampleEvent(Guid.NewGuid(), Guid.NewGuid(), "N-1", "Individual", null, 0, false),
            OutboxJson.Options);

        payload.Should().Contain("\"clientType\"").And.NotContain("\"ClientType\"");
    }

    [Fact]
    public void Reading_a_camel_case_payload_with_default_options_silently_loses_everything()
    {
        // This is the bug itself, pinned. It documents WHY OutboxJson exists: the failure is not
        // an exception, it is a fully-formed object with nothing in it. Anyone tempted to hand
        // JsonSerializer its defaults on either side of the outbox should read this test first.
        var payload = JsonSerializer.Serialize(
            new SampleEvent(Guid.NewGuid(), Guid.NewGuid(), "N-1", "Legal", Guid.NewGuid(), 5, true),
            OutboxJson.Options);

        var restored = (SampleEvent)JsonSerializer.Deserialize(payload, typeof(SampleEvent))!;

        restored.Should().NotBeNull("deserialization does not throw — that is the whole problem");
        restored.ClientType.Should().BeNull();
        restored.ClientNumber.Should().BeNull();
        restored.TenantId.Should().BeEmpty();
        restored.Count.Should().Be(0);
    }
}
