namespace Sankore.Modules.Notifications.Tests.Features.Webhooks.Parsers;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.Webhooks.Parsers;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

public sealed class PostmarkWebhookParserTests
{
    private readonly PostmarkWebhookParser _parser = new(NullTestLogger<PostmarkWebhookParser>.Instance);

    [Fact]
    public void ProviderKey_is_postmark()
    {
        _parser.ProviderKey.Should().Be("postmark");
    }

    [Fact]
    public void Parses_Delivery_event()
    {
        var body = """
            {
              "RecordType": "Delivery",
              "MessageID": "pm-msg-1",
              "Recipient": "user@example.com",
              "DeliveredAt": "2024-01-01T00:00:00Z"
            }
            """;

        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Delivered);
        events[0].RecipientEmail.Should().Be("user@example.com");
        events[0].ExternalMessageId.Should().Be("pm-msg-1");
    }

    [Fact]
    public void Parses_Bounce_event()
    {
        var body = """
            {
              "RecordType": "Bounce",
              "MessageID": "pm-msg-2",
              "Email": "bounce@example.com",
              "Type": 1
            }
            """;

        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Bounced);
        events[0].RecipientEmail.Should().Be("bounce@example.com");
    }

    [Fact]
    public void Parses_SpamComplaint_event()
    {
        var body = """
            {
              "RecordType": "SpamComplaint",
              "MessageID": "pm-msg-3",
              "Email": "spam@example.com"
            }
            """;

        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Complained);
    }

    [Fact]
    public void Returns_empty_for_unrelated_record_types()
    {
        var body = """{"RecordType": "Open", "MessageID": "pm-1", "Recipient": "x@x.com"}""";
        var events = _parser.Parse(body);
        events.Should().BeEmpty();
    }

    [Fact]
    public void Returns_empty_for_malformed_json()
    {
        var events = _parser.Parse("not-json");
        events.Should().BeEmpty();
    }
}
