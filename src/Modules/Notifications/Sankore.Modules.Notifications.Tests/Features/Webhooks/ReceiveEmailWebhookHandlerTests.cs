namespace Sankore.Modules.Notifications.Tests.Features.Webhooks;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.Webhooks;
using Sankore.Modules.Notifications.Features.Webhooks.ReceiveEmailWebhook;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

public sealed class ReceiveEmailWebhookHandlerTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public async Task Creates_delivery_log_for_parsed_events()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);

        var parser = Substitute.For<IWebhookParser>();
        parser.ProviderKey.Returns("ses");
        parser.Parse(Arg.Any<string>()).Returns([
            new ParsedWebhookEvent(EmailDeliveryEventType.Delivered, "user@example.com", "ext-msg-1")
        ]);

        var handler = new ReceiveEmailWebhookHandler(
            db, [parser], NullTestLogger<ReceiveEmailWebhookHandler>.Instance);

        var result = await handler.Handle(
            new ReceiveEmailWebhookCommand(_tenantId, "ses", """{"payload":"test"}"""),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Verify in same context — InMemory data is visible after SaveChanges
        var log = db.EmailDeliveryLogs.FirstOrDefault();
        log.Should().NotBeNull();
        log!.EventType.Should().Be(EmailDeliveryEventType.Delivered);
        log.RecipientEmail.Should().Be("user@example.com");
        log.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task Returns_ok_when_no_parser_matches_provider()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var handler = new ReceiveEmailWebhookHandler(
            db, [], NullTestLogger<ReceiveEmailWebhookHandler>.Instance);

        var result = await handler.Handle(
            new ReceiveEmailWebhookCommand(_tenantId, "unknown-provider", "{}"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.EmailDeliveryLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_ok_and_persists_nothing_when_parser_returns_no_events()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);

        var parser = Substitute.For<IWebhookParser>();
        parser.ProviderKey.Returns("postmark");
        parser.Parse(Arg.Any<string>()).Returns(Array.Empty<ParsedWebhookEvent>());

        var handler = new ReceiveEmailWebhookHandler(
            db, [parser], NullTestLogger<ReceiveEmailWebhookHandler>.Instance);

        var result = await handler.Handle(
            new ReceiveEmailWebhookCommand(_tenantId, "postmark", """{"RecordType":"Open"}"""),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.EmailDeliveryLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Creates_multiple_logs_for_multiple_events()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);

        var parser = Substitute.For<IWebhookParser>();
        parser.ProviderKey.Returns("sendgrid");
        parser.Parse(Arg.Any<string>()).Returns([
            new ParsedWebhookEvent(EmailDeliveryEventType.Delivered, "a@x.com"),
            new ParsedWebhookEvent(EmailDeliveryEventType.Bounced, "b@x.com"),
        ]);

        var handler = new ReceiveEmailWebhookHandler(
            db, [parser], NullTestLogger<ReceiveEmailWebhookHandler>.Instance);

        await handler.Handle(
            new ReceiveEmailWebhookCommand(_tenantId, "sendgrid", "[]"),
            CancellationToken.None);

        db.EmailDeliveryLogs.Should().HaveCount(2);
    }
}
