namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle.Consumers;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

public sealed class KycValidatedConsumerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CreatorId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly FakeInboxGuard _inbox = new();
    private readonly RecordingEventPublisher _publisher = new();

    public void Dispose() => _factory.Dispose();

    private KycValidatedConsumer BuildConsumer() => new(
        _factory.CreateContext(),
        _inbox,
        _publisher,
        NullLogger<KycValidatedConsumer>.Instance);

    private static ConsumeContext<KycValidatedEvent> Context(KycValidatedEvent evt, Guid? messageId = null)
    {
        var context = Substitute.For<ConsumeContext<KycValidatedEvent>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(messageId);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private async Task SeedAsync(Client client)
    {
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Activates_a_client_waiting_for_kyc_and_publishes_the_activation_event()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycValidatedEvent(TenantId, client.Id, DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);
        stored.Status.Should().Be(ClientStatus.Active);
        stored.KycStatus.Should().Be(KycStatus.Approved);

        _publisher.OfType<ClientActivatedEvent>().Should().ContainSingle()
            .Which.Should().Match<ClientActivatedEvent>(e =>
                e.TenantId == TenantId && e.ClientId == client.Id);
    }

    [Fact]
    public async Task Records_the_activation_in_the_status_history_under_the_SYSTEM_actor()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycValidatedEvent(TenantId, client.Id, DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var line = await assertions.ClientStatusHistories
            .Where(h => h.ClientId == client.Id && h.NewStatus == ClientStatus.Active)
            .SingleAsync();

        line.OldStatus.Should().Be(ClientStatus.PendingKyc);
        // The SYSTEM account, not the operator who created the client.
        line.ActorUserId.Should().Be(Guid.Empty);
        line.ActorUserId.Should().NotBe(CreatorId);
    }

    [Fact]
    public async Task Only_refreshes_the_kyc_status_of_a_suspended_client_without_changing_its_status()
    {
        var client = ClientBuilder.Suspended(TenantId, AgencyId, CreatorId);
        // Make the KYC status non-approved so the refresh is observable.
        client.ApplyKycRejected("Blurred document", DateTimeOffset.UtcNow, CreatorId);
        await SeedAsync(client);

        var evt = new KycValidatedEvent(TenantId, client.Id, DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.KycStatus.Should().Be(KycStatus.Approved);
        stored.Status.Should().Be(ClientStatus.Suspended, "a late KYC approval must not lift a suspension");

        _publisher.OfType<ClientActivatedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Only_refreshes_the_kyc_status_of_an_archived_client_without_resurrecting_it()
    {
        var client = ClientBuilder.Archived(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycValidatedEvent(TenantId, client.Id, DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.Status.Should().Be(ClientStatus.Archived);
        stored.KycStatus.Should().Be(KycStatus.Approved);
        _publisher.OfType<ClientActivatedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_redelivery_changes_nothing_a_second_time()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycValidatedEvent(TenantId, client.Id, DateTimeOffset.UtcNow);
        var messageId = Guid.NewGuid();

        await BuildConsumer().Consume(Context(evt, messageId));
        await BuildConsumer().Consume(Context(evt, messageId));

        await using var assertions = _factory.CreateContext();

        // Exactly one activation line, exactly one activation event.
        var lines = await assertions.ClientStatusHistories
            .CountAsync(h => h.ClientId == client.Id && h.NewStatus == ClientStatus.Active);
        lines.Should().Be(1);

        _publisher.OfType<ClientActivatedEvent>().Should().HaveCount(1);
        _inbox.Calls.Should().Be(2, "the guard must be the first thing every delivery hits");
    }

    [Fact]
    public async Task Falls_back_on_the_event_id_when_the_broker_supplies_no_message_id()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycValidatedEvent(TenantId, client.Id, DateTimeOffset.UtcNow);

        await BuildConsumer().Consume(Context(evt, messageId: null));
        await BuildConsumer().Consume(Context(evt, messageId: null));

        await using var assertions = _factory.CreateContext();
        var lines = await assertions.ClientStatusHistories
            .CountAsync(h => h.ClientId == client.Id && h.NewStatus == ClientStatus.Active);

        lines.Should().Be(1);
    }

    [Fact]
    public async Task Ignores_an_event_aimed_at_a_client_of_another_tenant()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        // Same client id, wrong tenant: the explicit TenantId predicate must not match.
        var evt = new KycValidatedEvent(OtherTenantId, client.Id, DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.Status.Should().Be(ClientStatus.PendingKyc);
        _publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Ignores_an_event_for_an_unknown_client()
    {
        var evt = new KycValidatedEvent(TenantId, Guid.NewGuid(), DateTimeOffset.UtcNow);

        var consume = async () => await BuildConsumer().Consume(Context(evt));

        await consume.Should().NotThrowAsync();
        _publisher.Published.Should().BeEmpty();
    }
}
