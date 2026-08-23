namespace Sankore.Modules.Notifications.Tests.Features.Webhooks.Parsers;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.Webhooks.Parsers;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

public sealed class SendGridWebhookParserTests
{
    private readonly SendGridWebhookParser _parser = new(NullTestLogger<SendGridWebhookParser>.Instance);

    [Fact]
    public void ProviderKey_is_sendgrid()
    {
        _parser.ProviderKey.Should().Be("sendgrid");
    }

    [Fact]
    public void Parses_delivered_event()
    {
        var body = """[{"event":"delivered","email":"user@example.com","sg_message_id":"sg-1"}]""";
        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Delivered);
        events[0].RecipientEmail.Should().Be("user@example.com");
        events[0].ExternalMessageId.Should().Be("sg-1");
    }

    [Fact]
    public void Parses_bounce_event()
    {
        var body = """[{"event":"bounce","email":"bad@example.com","sg_message_id":"sg-2"}]""";
        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Bounced);
    }

    [Fact]
    public void Parses_dropped_as_Rejected()
    {
        var body = """[{"event":"dropped","email":"x@example.com","sg_message_id":"sg-3"}]""";
        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Rejected);
    }

    [Fact]
    public void Parses_spamreport_as_Complained()
    {
        var body = """[{"event":"spamreport","email":"y@example.com","sg_message_id":"sg-4"}]""";
        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Complained);
    }

    [Fact]
    public void Ignores_open_and_click_events()
    {
        var body = """
            [
              {"event":"open","email":"a@x.com","sg_message_id":"sg-5"},
              {"event":"click","email":"b@x.com","sg_message_id":"sg-6"},
              {"event":"delivered","email":"c@x.com","sg_message_id":"sg-7"}
            ]
            """;

        var events = _parser.Parse(body);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(EmailDeliveryEventType.Delivered);
        events[0].RecipientEmail.Should().Be("c@x.com");
    }

    [Fact]
    public void Parses_multiple_delivery_outcome_events_in_one_payload()
    {
        var body = """
            [
              {"event":"delivered","email":"a@x.com","sg_message_id":"sg-1"},
              {"event":"bounce","email":"b@x.com","sg_message_id":"sg-2"}
            ]
            """;

        var events = _parser.Parse(body);

        events.Should().HaveCount(2);
    }

    [Fact]
    public void Returns_empty_for_malformed_json()
    {
        var events = _parser.Parse("not json");
        events.Should().BeEmpty();
    }

    [Fact]
    public void Returns_empty_for_non_array_payload()
    {
        var events = _parser.Parse("""{"event":"delivered","email":"x@x.com"}""");
        events.Should().BeEmpty();
    }
}
