namespace Sankore.Modules.Notifications.Tests.Features.InApp;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Sankore.Shared.Kernel.Realtime;
using Xunit;

/// <summary>The bell (WA-FE-10): one row per key, a nudge after the commit, internal links only.</summary>
public sealed class InAppNotificationTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private InAppNotificationRequest Request(string key = "k-1", string? link = "/leads/1/suivi/whatsapp") => new(
        _tenantId, _userId, "whatsapp.escalation", "Un prospect attend", "Awa a écrit.", link, key);

    [Fact]
    public async Task The_same_key_notifies_once_and_each_one_is_pushed()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var realtime = Substitute.For<IRealtimeNotifier>();
        var facade = new NotificationsModuleFacade(db, realtime);

        var first = await facade.NotifyInAppAsync(Request());
        var again = await facade.NotifyInAppAsync(Request());

        again.Value.Should().Be(first.Value, "a replayed consumer must not ring twice");
        (await db.UserNotifications.IgnoreQueryFilters().CountAsync()).Should().Be(1);

        await realtime.Received(1).PushToUserAsync(
            _tenantId, _userId, NotificationsModuleFacade.InAppCreatedEvent, Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example")]
    public async Task A_link_outside_the_CRM_is_dropped(string link)
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var facade = new NotificationsModuleFacade(db);

        await facade.NotifyInAppAsync(Request(link: link));

        (await db.UserNotifications.IgnoreQueryFilters().SingleAsync()).Link.Should().BeNull();
    }
}
