namespace Sankore.Modules.Notifications.Tests.Features.QueueEmail;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Xunit;

public sealed class QueueEmailTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private QueueEmailRequest BuildRequest(string? idempotencyKey = null) => new(
        TenantId: _tenantId,
        Module: "Leads",
        TemplateKey: "welcome",
        Locale: "fr",
        RecipientEmail: "user@example.com",
        RecipientName: "Test User",
        TemplateData: new Dictionary<string, object> { ["name"] = "Test" },
        IdempotencyKey: idempotencyKey ?? $"idem-{Guid.NewGuid()}");

    [Fact]
    public async Task QueueEmail_creates_pending_outbox_message()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var facade = new NotificationsModuleFacade(db);

        var result = await facade.QueueEmailAsync(BuildRequest());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);

        // Verify in same context — data is visible after SaveChanges
        var msg = db.EmailOutboxMessages.FirstOrDefault(m => m.Id == result.Value);
        msg.Should().NotBeNull();
        msg!.Status.Should().Be(EmailOutboxStatus.Pending);
        msg.TenantId.Should().Be(_tenantId);
        msg.TemplateKey.Should().Be("welcome");
        msg.RecipientEmail.Should().Be("user@example.com");
    }

    [Fact]
    public async Task QueueEmail_returns_failure_on_duplicate_idempotency_key()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var facade = new NotificationsModuleFacade(db);

        var key = $"idem-{Guid.NewGuid()}";

        // First call succeeds
        var first = await facade.QueueEmailAsync(BuildRequest(key));
        first.IsSuccess.Should().BeTrue();

        // Second call with same key on same db should detect duplicate
        var second = await facade.QueueEmailAsync(BuildRequest(key));
        second.IsFailure.Should().BeTrue();
        second.Error.Should().Contain("DUPLICATE");
    }
}
