namespace Sankore.Modules.Administration.Tests.Features.NotificationSettings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Xunit;

/// <summary>
/// The quota is the one field where a lost update means sending past a limit an institution
/// deliberately set, so check-and-increment is tested as a single operation.
/// </summary>
public sealed class EmailQuotaTests
{
    private static TenantNotificationSettings WithLimit(int? limit)
    {
        var settings = TenantNotificationSettings.CreateDefault(Guid.NewGuid(), Guid.NewGuid());
        settings.SetMonthlyQuota(limit, Guid.NewGuid());
        return settings;
    }

    [Fact]
    public void Grants_and_counts_while_under_the_limit()
    {
        var settings = WithLimit(3);

        settings.TryConsumeMonthlyQuota().Should().BeTrue();
        settings.TryConsumeMonthlyQuota().Should().BeTrue();

        settings.CurrentMonthUsageCount.Should().Be(2);
    }

    [Fact]
    public void Refuses_once_the_limit_is_reached_and_stops_counting()
    {
        var settings = WithLimit(2);

        settings.TryConsumeMonthlyQuota().Should().BeTrue();
        settings.TryConsumeMonthlyQuota().Should().BeTrue();

        settings.TryConsumeMonthlyQuota().Should().BeFalse();
        settings.CurrentMonthUsageCount.Should().Be(2, "a refused message did not consume anything");
    }

    [Fact]
    public void An_unlimited_tenant_is_always_granted_but_still_counted()
    {
        // Counting without a limit is what lets an administrator see real usage before deciding
        // what the limit should be.
        var settings = WithLimit(null);

        for (var i = 0; i < 50; i++)
            settings.TryConsumeMonthlyQuota().Should().BeTrue();

        settings.CurrentMonthUsageCount.Should().Be(50);
    }

    [Fact]
    public void The_counter_starts_again_when_the_month_turns()
    {
        var settings = WithLimit(1);
        settings.TryConsumeMonthlyQuota().Should().BeTrue();
        settings.TryConsumeMonthlyQuota().Should().BeFalse();

        // Rewind the tracked month to last month, as the passage of time would.
        typeof(TenantNotificationSettings)
            .GetProperty(nameof(TenantNotificationSettings.CurrentMonthStartedAt))!
            .SetValue(settings, DateTimeOffset.UtcNow.AddMonths(-1));

        settings.TryConsumeMonthlyQuota().Should().BeTrue("a new month is a new allowance");
        settings.CurrentMonthUsageCount.Should().Be(1, "the count restarted rather than accumulating");
    }

    [Fact]
    public void Raising_the_limit_unblocks_a_tenant_immediately()
    {
        var settings = WithLimit(1);
        settings.TryConsumeMonthlyQuota().Should().BeTrue();
        settings.TryConsumeMonthlyQuota().Should().BeFalse();

        settings.SetMonthlyQuota(5, Guid.NewGuid());

        settings.TryConsumeMonthlyQuota().Should().BeTrue();
    }
}
