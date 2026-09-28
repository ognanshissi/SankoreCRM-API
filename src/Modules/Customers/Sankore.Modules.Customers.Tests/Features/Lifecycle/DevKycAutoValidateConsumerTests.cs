namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle.Consumers;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

public sealed class DevKycAutoValidateConsumerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ClientId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid CreatorId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly FakeInboxGuard _inbox = new();
    private readonly RecordingEventPublisher _publisher = new();

    public void Dispose() => _factory.Dispose();

    private DevKycAutoValidateConsumer BuildConsumer(string environmentName, bool stubEnabled) => new(
        _factory.CreateContext(),
        LifecycleTestDoubles.Environment(environmentName),
        TestDoubles.Settings(TenantId, (CustomerSettingKeys.KycStubEnabled, stubEnabled ? "true" : "false")),
        _inbox,
        _publisher,
        NullLogger<DevKycAutoValidateConsumer>.Instance);

    private static ConsumeContext<ClientCreatedEvent> Context(Guid? messageId = null)
    {
        var evt = new ClientCreatedEvent(
            TenantId, ClientId, "AG000001-2026-000001", nameof(ClientType.Individual),
            AgencyId, null, null, CreatorId);

        var context = Substitute.For<ConsumeContext<ClientCreatedEvent>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(messageId);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task Auto_approves_kyc_in_development_when_the_tenant_enabled_the_stub()
    {
        await BuildConsumer("Development", stubEnabled: true).Consume(Context());

        _publisher.OfType<KycValidatedEvent>().Should().ContainSingle()
            .Which.Should().Match<KycValidatedEvent>(e =>
                e.TenantId == TenantId && e.CustomerEntityId == ClientId);
    }

    [Fact]
    public async Task Does_nothing_in_development_when_the_tenant_disabled_the_stub()
    {
        await BuildConsumer("Development", stubEnabled: false).Consume(Context());

        _publisher.Published.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Does_nothing_outside_development_even_when_the_tenant_setting_says_yes(string environment)
    {
        await BuildConsumer(environment, stubEnabled: true).Consume(Context());

        _publisher.Published.Should().BeEmpty();

        // The environment gate runs first: not even an inbox row is burnt, so the
        // stub leaves no trace at all in a non-development deployment.
        _inbox.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_redelivery_approves_only_once()
    {
        var messageId = Guid.NewGuid();

        await BuildConsumer("Development", stubEnabled: true).Consume(Context(messageId));
        await BuildConsumer("Development", stubEnabled: true).Consume(Context(messageId));

        _publisher.OfType<KycValidatedEvent>().Should().HaveCount(1);
    }

    [Fact]
    public async Task Claims_its_own_inbox_slot_so_the_other_consumers_of_the_same_event_are_not_starved()
    {
        // ClientCreatedEvent is also consumed by the timeline projection, and the
        // inbox primary key IS the event id — guarding on the raw message id would let
        // whichever consumer runs first swallow the event for the others.
        var messageId = Guid.NewGuid();

        await BuildConsumer("Development", stubEnabled: true).Consume(Context(messageId));

        var stillAvailable = await _inbox.TryBeginAsync(
            messageId, TenantId, nameof(ClientCreatedEvent), CancellationToken.None);

        stillAvailable.Should().BeTrue();
    }
}
