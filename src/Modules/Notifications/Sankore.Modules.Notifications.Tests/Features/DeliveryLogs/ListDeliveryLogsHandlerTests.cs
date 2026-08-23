namespace Sankore.Modules.Notifications.Tests.Features.DeliveryLogs;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.DeliveryLogs.ListDeliveryLogs;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

public sealed class ListDeliveryLogsHandlerTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private EmailDeliveryLog MakeLog(string email, EmailDeliveryEventType type, DateTimeOffset? at = null)
        => EmailDeliveryLog.Record(_tenantId, null, type, email, "{}");

    [Fact]
    public async Task Returns_only_current_tenant_logs()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        db.EmailDeliveryLogs.Add(MakeLog("mine@x.com", EmailDeliveryEventType.Delivered));

        var otherDb = TestNotificationsDbContextFactory.Create(Guid.NewGuid());
        otherDb.EmailDeliveryLogs.Add(
            EmailDeliveryLog.Record(Guid.NewGuid(), null, EmailDeliveryEventType.Bounced, "other@x.com", "{}"));
        await otherDb.SaveChangesAsync();

        await db.SaveChangesAsync();

        var handler = new ListDeliveryLogsHandler(db);
        var result = await handler.Handle(new ListDeliveryLogsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(l => l.RecipientEmail == "mine@x.com");
    }

    [Fact]
    public async Task Filters_by_recipient_email()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        db.EmailDeliveryLogs.AddRange(
            MakeLog("alice@x.com", EmailDeliveryEventType.Delivered),
            MakeLog("bob@x.com", EmailDeliveryEventType.Bounced));
        await db.SaveChangesAsync();

        var handler = new ListDeliveryLogsHandler(db);
        var result = await handler.Handle(
            new ListDeliveryLogsQuery(RecipientEmail: "alice@x.com"), CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.RecipientEmail.Should().Be("alice@x.com");
    }

    [Fact]
    public async Task Filters_by_event_type()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        db.EmailDeliveryLogs.AddRange(
            MakeLog("a@x.com", EmailDeliveryEventType.Delivered),
            MakeLog("b@x.com", EmailDeliveryEventType.Bounced),
            MakeLog("c@x.com", EmailDeliveryEventType.Bounced));
        await db.SaveChangesAsync();

        var handler = new ListDeliveryLogsHandler(db);
        var result = await handler.Handle(
            new ListDeliveryLogsQuery(EventType: EmailDeliveryEventType.Bounced), CancellationToken.None);

        result.Value.Items.Should().HaveCount(2);
        result.Value.Items.Should().AllSatisfy(l => l.EventType.Should().Be("Bounced"));
    }

    [Fact]
    public async Task Paginates_results()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        for (var i = 0; i < 10; i++)
            db.EmailDeliveryLogs.Add(MakeLog($"user{i}@x.com", EmailDeliveryEventType.Delivered));
        await db.SaveChangesAsync();

        var handler = new ListDeliveryLogsHandler(db);
        var result = await handler.Handle(
            new ListDeliveryLogsQuery(Page: 1, PageSize: 3), CancellationToken.None);

        result.Value.Items.Should().HaveCount(3);
        result.Value.TotalCount.Should().Be(10);
        result.Value.PageSize.Should().Be(3);
    }

    [Fact]
    public async Task Returns_total_count_correctly()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        db.EmailDeliveryLogs.AddRange(
            MakeLog("a@x.com", EmailDeliveryEventType.Delivered),
            MakeLog("b@x.com", EmailDeliveryEventType.Delivered));
        await db.SaveChangesAsync();

        var handler = new ListDeliveryLogsHandler(db);
        var result = await handler.Handle(
            new ListDeliveryLogsQuery(Page: 1, PageSize: 50), CancellationToken.None);

        result.Value.TotalCount.Should().Be(2);
    }
}
