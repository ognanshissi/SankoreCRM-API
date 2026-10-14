namespace Sankore.Modules.Notifications.Tests.Features.QueueSms;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

/// <summary>
/// Queuing a text message. Same contract as the email queue, with one addition that matters on a
/// per-message channel: a destination is refused synchronously rather than discovered at send
/// time, because a caller that failed to resolve a telephone number should learn it now and not
/// through a dead-letter row ten seconds later.
/// </summary>
public sealed class QueueSmsTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private QueueSmsRequest Request(
        string? idempotencyKey = null, string phone = "+2250708091801") => new(
        TemplateKey: "kyc.remote-otp",
        RecipientPhone: phone,
        Module: "Customers",
        Locale: "fr",
        TemplateData: new Dictionary<string, object> { ["code"] = "123456" },
        IdempotencyKey: idempotencyKey ?? $"idem-{Guid.NewGuid()}",
        TenantId: _tenantId);

    [Fact]
    public async Task Queuing_creates_a_pending_message()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var facade = new NotificationsModuleFacade(db);

        var result = await facade.QueueSmsAsync(Request());

        result.IsSuccess.Should().BeTrue();

        var message = db.SmsOutboxMessages.FirstOrDefault(m => m.Id == result.Value);

        message.Should().NotBeNull();
        message!.Status.Should().Be(SmsOutboxStatus.Pending);
        message.TenantId.Should().Be(_tenantId);
        message.RecipientPhone.Should().Be("+2250708091801");

        // Not yet handed to anybody: the provider fills these in, and a non-null value here
        // before a send would mean the queue was claiming something it had not done.
        message.ProviderMessageId.Should().BeNull();
        message.ProviderStatus.Should().BeNull();
        message.SegmentCount.Should().Be(0);
    }

    [Fact]
    public async Task A_duplicate_key_is_dropped()
    {
        // On this channel that is the difference between one charge and two, which is why every
        // caller is expected to key on something stable.
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var facade = new NotificationsModuleFacade(db);

        var key = $"idem-{Guid.NewGuid()}";

        (await facade.QueueSmsAsync(Request(key))).IsSuccess.Should().BeTrue();

        var second = await facade.QueueSmsAsync(Request(key));

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be("DUPLICATE_IDEMPOTENCY_KEY");

        db.SmsOutboxMessages.Count(m => m.IdempotencyKey == key).Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_message_with_no_destination_is_refused_here_and_not_at_send_time(string phone)
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var facade = new NotificationsModuleFacade(db);

        var result = await facade.QueueSmsAsync(Request(phone: phone));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("RECIPIENT_PHONE_REQUIRED");

        db.SmsOutboxMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task The_two_queues_are_separate_tables()
    {
        // The reason SmsOutboxMessage is its own entity: one table with a channel column would
        // mean two processors competing for the same hot rows and a filter whose absence sends an
        // SMS through the email sender.
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var facade = new NotificationsModuleFacade(db);

        await facade.QueueSmsAsync(Request());

        db.SmsOutboxMessages.Should().HaveCount(1);
        db.EmailOutboxMessages.Should().BeEmpty();
    }
}
