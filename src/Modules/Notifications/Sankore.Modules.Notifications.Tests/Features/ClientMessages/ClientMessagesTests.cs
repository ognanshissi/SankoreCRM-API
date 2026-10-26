namespace Sankore.Modules.Notifications.Tests.Features.ClientMessages;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Features.ClientMessages;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The messages a client was sent, as the client record lists them (product owner, 2026-10-26).
/// </summary>
public sealed class ClientMessagesTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();

    private static Dictionary<string, object> Data() => new() { ["full_name"] = "Awa Koné" };

    [Fact]
    public async Task A_message_queued_for_a_client_is_listed_for_that_client_only_with_a_masked_recipient()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenant);
        var facade = new NotificationsModuleFacade(db);

        (await facade.QueueEmailAsync(new QueueEmailRequest(
            "client.welcome", "awa.kone@example.ci", "Awa Koné", "Customers", "fr", Data(), "k1", _tenant, _client))).IsSuccess.Should().BeTrue();
        (await facade.QueueSmsAsync(new QueueSmsRequest(
            "kyc.remote-otp", "+2250700000001", "Customers", "fr", Data(), "k2", _tenant, _client))).IsSuccess.Should().BeTrue();
        (await facade.QueueEmailAsync(new QueueEmailRequest(
            "client.welcome", "autre@example.ci", null, "Customers", "fr", Data(), "k3", _tenant, Guid.NewGuid()))).IsSuccess.Should().BeTrue();
        (await facade.QueueEmailAsync(new QueueEmailRequest(
            "password.reset", "agent@example.ci", null, "Administration", "fr", Data(), "k4", _tenant))).IsSuccess.Should().BeTrue();

        var messages = await facade.ListClientMessagesAsync(_tenant, _client);

        messages.Should().HaveCount(2);
        messages.Single(m => m.Channel == "Email").Recipient.Should().Be("a•••@example.ci");
        messages.Single(m => m.Channel == "Sms").Recipient.Should().Be("+225••••01");
        messages.Should().OnlyContain(m => m.Status == "Pending");
    }

    [Fact]
    public void A_recipient_already_redacted_by_the_processor_passes_through()
        => ClientMessageMasking.Phone("+225•••••01").Should().Be("+225•••••01");

    [Fact]
    public async Task Once_the_content_is_purged_the_trace_stays_without_a_recipient()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenant);
        var facade = new NotificationsModuleFacade(db);
        await facade.QueueEmailAsync(new QueueEmailRequest(
            "client.welcome", "awa.kone@example.ci", "Awa Koné", "Customers", "fr", Data(), "k1", _tenant, _client));

        // What the purge's ExecuteUpdate does, which InMemory cannot run.
        var row = await db.EmailOutboxMessages.AsTracking().SingleAsync();
        db.Entry(row).Property(m => m.RecipientEmail).CurrentValue = string.Empty;
        db.Entry(row).Property(m => m.ContentPurgedAt).CurrentValue = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var trace = (await facade.ListClientMessagesAsync(_tenant, _client)).Should().ContainSingle().Subject;
        trace.TemplateKey.Should().Be("client.welcome");
        trace.Recipient.Should().BeNull();
    }

    [Fact]
    public async Task The_erasure_translates_to_sql()
    {
        await using var db = new NotificationsDbContext(
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Timeout=1;Database=none;Username=none;Password=none")
                .UseSnakeCaseNamingConvention()
                .Options,
            new FixedTenantContext(_tenant));

        var thrown = await Record.ExceptionAsync(() => ClientMessageErasure.EraseAsync(db, _tenant, _client, default));

        // A query EF cannot translate throws before any connection: failing ON the connection proves
        // the delete translates (same proof as TechnicalPurgeSqlTranslationTests).
        var reachedTheNetwork = false;
        for (var e = thrown; e is not null; e = e.InnerException)
            reachedTheNetwork |= e is Npgsql.NpgsqlException;
        reachedTheNetwork.Should().BeTrue(thrown?.ToString());
    }
}
