namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle.Consumers;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

public sealed class KycRejectedConsumerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CreatorId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly FakeInboxGuard _inbox = new();

    public void Dispose() => _factory.Dispose();

    private KycRejectedConsumer BuildConsumer() => new(
        _factory.CreateContext(), _inbox, NullLogger<KycRejectedConsumer>.Instance);

    private static ConsumeContext<KycRejectedEvent> Context(KycRejectedEvent evt, Guid? messageId = null)
    {
        var context = Substitute.For<ConsumeContext<KycRejectedEvent>>();
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
    public async Task Moves_a_client_waiting_for_kyc_to_KycRejected_and_keeps_the_motive()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycRejectedEvent(TenantId, client.Id, "Expired document", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.Status.Should().Be(ClientStatus.KycRejected);
        stored.KycStatus.Should().Be(KycStatus.Rejected);
        stored.KycRejectionReason.Should().Be("Expired document");
    }

    [Fact]
    public async Task Records_the_rejection_in_the_status_history_with_the_motive_and_the_SYSTEM_actor()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycRejectedEvent(TenantId, client.Id, "Expired document", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var line = await assertions.ClientStatusHistories
            .Where(h => h.ClientId == client.Id && h.NewStatus == ClientStatus.KycRejected)
            .SingleAsync();

        line.OldStatus.Should().Be(ClientStatus.PendingKyc);
        line.Reason.Should().Be("Expired document");
        line.ActorUserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task Only_refreshes_the_kyc_status_of_a_suspended_client_without_changing_its_status()
    {
        var client = ClientBuilder.Suspended(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycRejectedEvent(TenantId, client.Id, "Periodic review failed", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.KycStatus.Should().Be(KycStatus.Rejected);
        stored.Status.Should().Be(ClientStatus.Suspended);
    }

    [Fact]
    public async Task A_redelivery_changes_nothing_a_second_time()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycRejectedEvent(TenantId, client.Id, "Expired document", DateTimeOffset.UtcNow);
        var messageId = Guid.NewGuid();

        await BuildConsumer().Consume(Context(evt, messageId));
        await BuildConsumer().Consume(Context(evt, messageId));

        await using var assertions = _factory.CreateContext();
        var lines = await assertions.ClientStatusHistories
            .CountAsync(h => h.ClientId == client.Id && h.NewStatus == ClientStatus.KycRejected);

        lines.Should().Be(1);
    }

    [Fact]
    public async Task Ignores_an_event_aimed_at_a_client_of_another_tenant()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        var evt = new KycRejectedEvent(OtherTenantId, client.Id, "Expired document", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.Status.Should().Be(ClientStatus.PendingKyc);
        stored.KycStatus.Should().NotBe(KycStatus.Rejected);
    }

    [Fact]
    public async Task Ignores_an_event_carrying_no_motive()
    {
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, CreatorId);
        await SeedAsync(client);

        // The aggregate answers REASON_REQUIRED; the consumer logs and drops the
        // message rather than persisting a rejection nobody can explain.
        var evt = new KycRejectedEvent(TenantId, client.Id, "   ", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.Status.Should().Be(ClientStatus.PendingKyc);
    }
}
