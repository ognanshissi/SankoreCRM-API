namespace Sankore.Modules.Customers.Tests.Features.LeadConversion;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.LeadConversion.Consumers;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class ClientWelcomeEmailConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly List<QueueEmailRequest> _sent = [];

    public ClientWelcomeEmailConsumerTests() => _factory = new TestCustomersDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private INotificationsModule Notifications()
    {
        var notifications = Substitute.For<INotificationsModule>();
        notifications
            .QueueEmailAsync(Arg.Do<QueueEmailRequest>(_sent.Add), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(Guid.NewGuid()));
        return notifications;
    }

    private ITenantStore TenantStore()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Sankore MFI", "sankore.ci", true, false, null, null, "fr"));
        return store;
    }

    private ClientWelcomeEmailConsumer Consumer(INotificationsModule? notifications = null)
    {
        var db = _factory.CreateContext();
        return new ClientWelcomeEmailConsumer(
            db,
            new InboxGuard(db),
            TestDoubles.Encryptor(),
            notifications ?? Notifications(),
            TenantStore(),
            NullLogger<ClientWelcomeEmailConsumer>.Instance);
    }

    /// <summary>Seeds a converted client, optionally with an email contact point.</summary>
    private async Task<Client> SeedClientAsync(Guid? sourceLeadId, string? email, string language = "fr")
    {
        await using var db = _factory.CreateContext();

        var client = TestClientFactory.Individual(
            _tenantId, _agencyId,
            clientNumber: "ABJ-PLT-2026-000042",
            preferredLanguage: language,
            sourceLeadId: sourceLeadId);

        if (email is not null)
        {
            var encryptor = TestDoubles.Encryptor();
            client.AddContactPoint(
                ContactPointType.Email,
                encryptor.Encrypt(email)!,
                blindIndex: "idx",
                label: null,
                isPrimary: true,
                validFrom: DateTimeOffset.UtcNow,
                actor: Guid.NewGuid());
        }

        return await TestClientFactory.SeedAsync(db, client);
    }

    private static ConsumeContext<ClientCreatedEvent> Context(Client client, Guid? sourceLeadId)
    {
        var evt = new ClientCreatedEvent(
            TenantId: client.TenantId,
            ClientId: client.Id,
            ClientNumber: client.ClientNumber,
            ClientType: client.Type.ToString(),
            AgencyId: client.AgencyId,
            AdvisorUserId: null,
            SourceLeadId: sourceLeadId,
            CreatedBy: Guid.NewGuid());

        var context = Substitute.For<ConsumeContext<ClientCreatedEvent>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(Guid.NewGuid());
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private async Task<int> InboxCountAsync()
    {
        await using var db = _factory.CreateContext();
        return db.InboxMessages.Count();
    }

    // ── welcoming ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Welcomes_a_prospect_who_became_a_client()
    {
        var leadId = Guid.NewGuid();
        var client = await SeedClientAsync(leadId, "awa@example.ci");

        await Consumer().Consume(Context(client, leadId));

        _sent.Should().ContainSingle();
        var mail = _sent[0];
        mail.TemplateKey.Should().Be(ClientWelcomeEmailConsumer.TemplateKey);
        mail.RecipientEmail.Should().Be("awa@example.ci", "the address is decrypted from the record");
        mail.RecipientName.Should().Be(client.DisplayName);
        mail.Module.Should().Be("Customers");
        mail.TenantId.Should().Be(_tenantId);
        mail.TemplateData["client_number"].Should().Be("ABJ-PLT-2026-000042");
        mail.TemplateData["company_name"].Should().Be("Sankore MFI");
    }

    [Fact]
    public async Task Honours_the_client_preferred_language()
    {
        var leadId = Guid.NewGuid();
        var client = await SeedClientAsync(leadId, "awa@example.ci", language: "en");

        await Consumer().Consume(Context(client, leadId));

        _sent.Should().ContainSingle();
        _sent[0].Locale.Should().Be("en");
    }

    // ── staying silent ──────────────────────────────────────────────────────

    [Fact]
    public async Task Says_nothing_for_a_client_keyed_in_directly()
    {
        // No SourceLeadId: the person is standing in front of an agent. A "welcome" mail for a
        // relationship that started ten minutes ago in person reads as spam.
        var client = await SeedClientAsync(sourceLeadId: null, email: "awa@example.ci");

        await Consumer().Consume(Context(client, sourceLeadId: null));

        _sent.Should().BeEmpty();
        (await InboxCountAsync()).Should().Be(0, "an event we ignore should not consume an inbox row");
    }

    [Fact]
    public async Task A_client_without_an_email_is_not_a_failure()
    {
        // Ordinary: a lead captured in the field often carries a phone number and nothing else.
        var leadId = Guid.NewGuid();
        var client = await SeedClientAsync(leadId, email: null);

        var act = async () => await Consumer().Consume(Context(client, leadId));

        await act.Should().NotThrowAsync();
        _sent.Should().BeEmpty();
        (await InboxCountAsync()).Should().Be(1,
            "the inbox row is still committed so a redelivery does not repeat the lookup forever");
    }

    // ── delivered at least once, welcomed exactly once ──────────────────────

    [Fact]
    public async Task A_redelivered_event_does_not_welcome_the_client_twice()
    {
        var leadId = Guid.NewGuid();
        var client = await SeedClientAsync(leadId, "awa@example.ci");
        var context = Context(client, leadId);

        await Consumer().Consume(context);
        await Consumer().Consume(context);   // same MessageId: the broker retried

        _sent.Should().ContainSingle();
    }

    [Fact]
    public async Task The_idempotency_key_is_per_client_not_per_message()
    {
        // Belt and braces: even two DIFFERENT deliveries of the same creation must collapse in
        // the notification outbox, so a re-created inbox table cannot produce a second welcome.
        var leadId = Guid.NewGuid();
        var client = await SeedClientAsync(leadId, "awa@example.ci");

        await Consumer().Consume(Context(client, leadId));

        _sent[0].IdempotencyKey.Should().Be(
            $"{ClientWelcomeEmailConsumer.TemplateKey}-{client.Id}");
    }

    [Fact]
    public async Task A_failing_notification_does_not_make_the_broker_redeliver_forever()
    {
        var leadId = Guid.NewGuid();
        var client = await SeedClientAsync(leadId, "awa@example.ci");

        var notifications = Substitute.For<INotificationsModule>();
        notifications
            .QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<Guid>>>(_ => throw new InvalidOperationException("SMTP is down"));

        var act = async () => await Consumer(notifications).Consume(Context(client, leadId));

        await act.Should().NotThrowAsync(
            "the client exists and the conversion stands; the mail is the only thing that failed");
        (await InboxCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_client_of_another_tenant_is_never_welcomed()
    {
        var leadId = Guid.NewGuid();
        var client = await SeedClientAsync(leadId, "awa@example.ci");

        var evt = new ClientCreatedEvent(
            TenantId: Guid.NewGuid(),           // someone else's tenant
            ClientId: client.Id,
            ClientNumber: client.ClientNumber,
            ClientType: client.Type.ToString(),
            AgencyId: client.AgencyId,
            AdvisorUserId: null,
            SourceLeadId: leadId,
            CreatedBy: Guid.NewGuid());

        var context = Substitute.For<ConsumeContext<ClientCreatedEvent>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(Guid.NewGuid());
        context.CancellationToken.Returns(CancellationToken.None);

        await Consumer().Consume(context);

        _sent.Should().BeEmpty("the lookup is scoped to the event's tenant, so nothing is found");
    }
}
