namespace Sankore.Modules.Notifications.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Xunit;

public sealed class EmailOutboxMessageTests
{
    private static EmailOutboxMessage Build(string key = "welcome") =>
        EmailOutboxMessage.Create(
            Guid.NewGuid(), "Leads", key, "fr",
            "to@example.com", "Recipient",
            "{}", $"idem-{Guid.NewGuid()}");

    [Fact]
    public void Create_sets_Pending_status_and_zero_attempts()
    {
        var msg = Build();

        msg.Status.Should().Be(EmailOutboxStatus.Pending);
        msg.AttemptCount.Should().Be(0);
        msg.SentAt.Should().BeNull();
        msg.LastError.Should().BeNull();
    }

    [Fact]
    public void MarkSending_sets_status_to_Sending()
    {
        var msg = Build();
        msg.MarkSending();

        msg.Status.Should().Be(EmailOutboxStatus.Sending);
        msg.LastAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkSent_sets_status_to_Sent_and_stamps_SentAt()
    {
        var msg = Build();
        msg.MarkSending();
        msg.MarkSent();

        msg.Status.Should().Be(EmailOutboxStatus.Sent);
        msg.SentAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkFailed_increments_attempt_and_stays_Failed_before_max()
    {
        var msg = Build();
        msg.MarkFailed("timeout", maxAttempts: 3);

        msg.AttemptCount.Should().Be(1);
        msg.Status.Should().Be(EmailOutboxStatus.Failed);
        msg.LastError.Should().Be("timeout");
    }

    [Fact]
    public void MarkFailed_at_max_attempts_transitions_to_DeadLettered()
    {
        var msg = Build();
        msg.MarkFailed("e1", 3);
        msg.MarkFailed("e2", 3);
        msg.MarkFailed("e3", 3);

        msg.Status.Should().Be(EmailOutboxStatus.DeadLettered);
        msg.AttemptCount.Should().Be(3);
    }

    [Fact]
    public void ResetForRetry_clears_error_and_resets_to_Pending()
    {
        var msg = Build();
        msg.MarkFailed("err", 3);
        msg.MarkFailed("err", 3);
        msg.MarkFailed("err", 3);
        msg.ResetForRetry();

        msg.Status.Should().Be(EmailOutboxStatus.Pending);
        msg.AttemptCount.Should().Be(0);
        msg.LastError.Should().BeNull();
    }
}
