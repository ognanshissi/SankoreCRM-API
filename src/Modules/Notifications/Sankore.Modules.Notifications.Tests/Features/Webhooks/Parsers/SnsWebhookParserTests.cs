namespace Sankore.Modules.Notifications.Tests.Features.Webhooks.Parsers;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.Webhooks.Parsers;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

public sealed class SnsWebhookParserTests
{
    private readonly SnsWebhookParser _parser = new(NullTestLogger<SnsWebhookParser>.Instance);

    [Fact]
    public void ProviderKey_is_ses()
    {
        _parser.ProviderKey.Should().Be("ses");
    }

    [Fact]
    public void Parses_Delivery_notification()
    {
        var body = SnsEnvelope("""
            {
              "notificationType": "Delivery",
              "mail": { "messageId": "msg-123", "destination": ["alice@example.com"] },
              "delivery": { "recipients": ["alice@example.com"], "timestamp": "2024-01-01T00:00:00Z" }
            }
            """);

        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Delivered);
        events[0].RecipientEmail.Should().Be("alice@example.com");
        events[0].ExternalMessageId.Should().Be("msg-123");
    }

    [Fact]
    public void Parses_Bounce_notification_with_multiple_recipients()
    {
        var body = SnsEnvelope("""
            {
              "notificationType": "Bounce",
              "mail": { "messageId": "msg-456", "destination": ["a@x.com","b@x.com"] },
              "bounce": {
                "bouncedRecipients": [
                  {"emailAddress": "a@x.com"},
                  {"emailAddress": "b@x.com"}
                ]
              }
            }
            """);

        var events = _parser.Parse(body);

        events.Should().HaveCount(2);
        events.Should().AllSatisfy(e => e.EventType.Should().Be(EmailDeliveryEventType.Bounced));
        events.Select(e => e.RecipientEmail).Should().BeEquivalentTo(["a@x.com", "b@x.com"]);
    }

    [Fact]
    public void Parses_Complaint_notification()
    {
        var body = SnsEnvelope("""
            {
              "notificationType": "Complaint",
              "mail": { "messageId": "msg-789", "destination": ["bob@example.com"] },
              "complaint": { "complainedRecipients": [{"emailAddress": "bob@example.com"}] }
            }
            """);

        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Complained);
    }

    [Fact]
    public void Parses_Reject_using_mail_destination()
    {
        var body = SnsEnvelope("""
            {
              "notificationType": "Reject",
              "mail": { "messageId": "msg-000", "destination": ["spam@example.com"] }
            }
            """);

        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Rejected);
        events[0].RecipientEmail.Should().Be("spam@example.com");
    }

    [Fact]
    public void Returns_empty_for_malformed_json()
    {
        var events = _parser.Parse("{not valid json");
        events.Should().BeEmpty();
    }

    [Fact]
    public void Returns_empty_when_notification_type_unknown()
    {
        var body = SnsEnvelope("""{ "notificationType": "Click" }""");
        var events = _parser.Parse(body);
        events.Should().BeEmpty();
    }

    private static string SnsEnvelope(string innerJson)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(innerJson);
        return $$"""{"Type":"Notification","Message":{{escaped}}}""";
    }
}
