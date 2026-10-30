namespace Sankore.Modules.Notifications.Tests.Infrastructure;

using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Modules.Notifications.Infrastructure.Processor;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Modules.Notifications.Infrastructure.Rendering;
using Sankore.Modules.Notifications.Infrastructure.Senders;
using Sankore.Modules.Notifications.Infrastructure.Sms;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

/// <summary>
/// Test mode, sandbox lot 1: the two queues refuse, BEFORE the allowance and the provider, a
/// message a test tenant may not send — and leave a live tenant's message on its usual path.
/// </summary>
public sealed class OutboxTestModeTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly SharedTestNotificationsDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IEmailSender _emailSender = Substitute.For<IEmailSender>();
    private readonly ISmsSender _smsSender = Substitute.For<ISmsSender>();
    private readonly ServiceProvider _services;

    public OutboxTestModeTests()
    {
        _factory = new SharedTestNotificationsDbContextFactory(_tenantId);

        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton(_admin);
        services.AddSingleton(_emailSender);
        services.AddSingleton(_smsSender);
        services.AddSingleton(Substitute.For<IEmailProviderResolver>());
        services.AddSingleton(Substitute.For<ITemplateRenderer>());
        services.AddSingleton(Substitute.For<ISmsProviderResolver>());
        services.AddSingleton(Substitute.For<ISmsTemplateRenderer>());
        _services = services.BuildServiceProvider();

        // A refused allowance ends a message that got past the test-mode check, without a sender.
        _admin.TryConsumeEmailQuotaAsync(_tenantId, Arg.Any<CancellationToken>()).Returns(new EmailQuotaDecision(false, 0, 0));
        _admin.TryConsumeSmsQuotaAsync(_tenantId, Arg.Any<CancellationToken>()).Returns(new SmsQuotaDecision(false, 0, 0, "SMS_QUOTA_EXCEEDED"));
    }

    [Fact]
    public async Task A_test_tenant_does_not_email_outside_its_team_nor_spend_its_allowance()
    {
        _admin.IsEmailBlockedInTestAsync(_tenantId, "client@gmail.com", Arg.Any<CancellationToken>()).Returns(true);
        var id = QueueEmail("client@gmail.com");

        await RunEmailAsync(id);

        var message = Email(id);
        message.Status.Should().Be(EmailOutboxStatus.DeadLettered);
        message.LastError.Should().StartWith(EmailOutboxMessage.TestModeBlocked);
        await _admin.DidNotReceive().TryConsumeEmailQuotaAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_live_tenants_email_goes_on_to_its_allowance()
    {
        var id = QueueEmail("client@gmail.com");

        await RunEmailAsync(id);

        Email(id).LastError.Should().StartWith("MONTHLY_QUOTA_EXCEEDED");
    }

    [Fact]
    public async Task A_test_tenant_does_not_text_outside_its_team_and_the_number_is_redacted()
    {
        _admin.IsPhoneBlockedInTestAsync(_tenantId, "+2250102030405", Arg.Any<CancellationToken>()).Returns(true);
        var id = QueueSms("+2250102030405");

        await RunSmsAsync(id);

        var message = Sms(id);
        message.Status.Should().Be(SmsOutboxStatus.DeadLettered);
        message.LastError.Should().StartWith(EmailOutboxMessage.TestModeBlocked);
        message.RecipientPhone.Should().NotBe("+2250102030405");
        await _admin.DidNotReceive().TryConsumeSmsQuotaAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _smsSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_live_tenants_text_goes_on_to_its_allowance()
    {
        var id = QueueSms("+2250102030405");

        await RunSmsAsync(id);

        Sms(id).LastError.Should().StartWith("SMS_QUOTA_EXCEEDED");
    }

    private Guid QueueEmail(string to)
    {
        var message = EmailOutboxMessage.Create(_tenantId, "Test", "test.key", "fr", to, null, "{}", Guid.NewGuid().ToString());
        using var db = _factory.CreateContext();
        db.EmailOutboxMessages.Add(message);
        db.SaveChanges();
        return message.Id;
    }

    private Guid QueueSms(string to)
    {
        var message = SmsOutboxMessage.Create(_tenantId, "Test", "test.key", "fr", to, "{}", Guid.NewGuid().ToString());
        using var db = _factory.CreateContext();
        db.SmsOutboxMessages.Add(message);
        db.SaveChanges();
        return message.Id;
    }

    private EmailOutboxMessage Email(Guid id)
    {
        using var db = _factory.CreateContext();
        return db.EmailOutboxMessages.Single(m => m.Id == id);
    }

    private SmsOutboxMessage Sms(Guid id)
    {
        using var db = _factory.CreateContext();
        return db.SmsOutboxMessages.Single(m => m.Id == id);
    }

    private Task RunEmailAsync(Guid id)
    {
        var processor = new EmailOutboxProcessor(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailOutboxProcessorOptions()),
            Options.Create(new PlatformEmailOptions()),
            NullLogger<EmailOutboxProcessor>.Instance);
        return Invoke(processor, id);
    }

    private Task RunSmsAsync(Guid id)
    {
        var processor = new SmsOutboxProcessor(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SmsOutboxProcessorOptions()),
            NullLogger<SmsOutboxProcessor>.Instance);
        return Invoke(processor, id);
    }

    /// <summary>One message, as a polling tick would process it — the loop around it is not the subject.</summary>
    private static Task Invoke(object processor, Guid id)
        => (Task)processor.GetType()
            .GetMethod("ProcessOneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(processor, [id, CancellationToken.None])!;
}
