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

public sealed class KycRiskLevelChangedConsumerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CreatorId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly FakeInboxGuard _inbox = new();

    public void Dispose() => _factory.Dispose();

    private KycRiskLevelChangedConsumer BuildConsumer() => new(
        _factory.CreateContext(), _inbox, NullLogger<KycRiskLevelChangedConsumer>.Instance);

    private static ConsumeContext<KycRiskLevelChangedEvent> Context(
        KycRiskLevelChangedEvent evt, Guid? messageId = null)
    {
        var context = Substitute.For<ConsumeContext<KycRiskLevelChangedEvent>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(messageId);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private async Task<Client> SeedActiveAsync()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, CreatorId);
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        await seed.SaveChangesAsync();
        return client;
    }

    [Theory]
    [InlineData("Low", RiskLevel.Low)]
    [InlineData("Medium", RiskLevel.Medium)]
    [InlineData("High", RiskLevel.High)]
    [InlineData("high", RiskLevel.High)] // the level name is parsed case-insensitively
    public async Task Applies_the_risk_level_and_leaves_the_status_untouched(string name, RiskLevel expected)
    {
        var client = await SeedActiveAsync();

        var evt = new KycRiskLevelChangedEvent(TenantId, client.Id, name, DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.RiskLevel.Should().Be(expected);
        stored.Status.Should().Be(ClientStatus.Active, "a risk rating is not a lifecycle decision");
    }

    [Fact]
    public async Task Writes_no_status_history_line_because_nothing_transitioned()
    {
        var client = await SeedActiveAsync();

        int before;
        await using (var assertions = _factory.CreateContext())
            before = await assertions.ClientStatusHistories.CountAsync(h => h.ClientId == client.Id);

        var evt = new KycRiskLevelChangedEvent(TenantId, client.Id, "High", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var after = _factory.CreateContext();
        var lines = await after.ClientStatusHistories.CountAsync(h => h.ClientId == client.Id);

        lines.Should().Be(before);
    }

    [Fact]
    public async Task Ignores_an_unknown_risk_level_rather_than_downgrading_the_client()
    {
        var client = await SeedActiveAsync();

        var high = new KycRiskLevelChangedEvent(TenantId, client.Id, "High", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(high, Guid.NewGuid()));

        var garbage = new KycRiskLevelChangedEvent(TenantId, client.Id, "CRITICAL", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(garbage, Guid.NewGuid()));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.RiskLevel.Should().Be(RiskLevel.High);
    }

    [Fact]
    public async Task A_redelivery_changes_nothing_a_second_time()
    {
        var client = await SeedActiveAsync();
        var messageId = Guid.NewGuid();

        var high = new KycRiskLevelChangedEvent(TenantId, client.Id, "High", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(high, messageId));

        // Same message id, different payload: the guard must short-circuit before the
        // payload is even looked at.
        var low = new KycRiskLevelChangedEvent(TenantId, client.Id, "Low", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(low, messageId));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.RiskLevel.Should().Be(RiskLevel.High);
    }

    [Fact]
    public async Task Ignores_an_event_aimed_at_a_client_of_another_tenant()
    {
        var client = await SeedActiveAsync();

        var evt = new KycRiskLevelChangedEvent(OtherTenantId, client.Id, "High", DateTimeOffset.UtcNow);
        await BuildConsumer().Consume(Context(evt));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);

        stored.RiskLevel.Should().NotBe(RiskLevel.High);
    }
}
