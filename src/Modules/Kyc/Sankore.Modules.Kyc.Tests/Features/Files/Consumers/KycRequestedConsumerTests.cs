namespace Sankore.Modules.Kyc.Tests.Features.Files.Consumers;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files.Consumers;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// The lead-conversion trigger, and the place where the two triggers meet: a converted lead
/// produces M01's <c>ClientCreatedEvent</c> AND M13's <c>KycRequestedIntegrationEvent</c> for the
/// same customer, and the pair must still leave exactly one file behind.
/// </summary>
public sealed class KycRequestedConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly ServiceProvider _sp;
    private readonly KycRequestedConsumer _consumer;

    public KycRequestedConsumerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
        _sp = ConsumerTestContainer.Build(_db);
        _consumer = new KycRequestedConsumer(
            new FixedScopeFactory(_sp), NullLogger<KycRequestedConsumer>.Instance);
    }

    public void Dispose()
    {
        _sp.Dispose();
        _db.Dispose();
        _factory.Dispose();
    }

    private KycRequestedIntegrationEvent Event(
        Guid customerId, Guid requestedBy, Guid? tenantId = null)
        => new(
            TenantId: tenantId ?? _tenantId,
            CustomerEntityId: customerId,
            LeadId: Guid.NewGuid(),
            FullName: "Awa Ouattara",
            PhoneNumber: "+2250708091801",
            Email: null,
            NationalId: null,
            DateOfBirth: null,
            RequestedBy: requestedBy);

    private Task<List<KycFile>> AllFiles()
        => _db.KycFiles.IgnoreQueryFilters().ToListAsync();

    [Fact]
    public async Task A_requested_kyc_always_opens_a_file_on_the_LeadConversion_channel()
    {
        // This event exists only on the conversion path, so the channel is a fact of the trigger
        // rather than something to derive from the payload.
        var customerId = Guid.NewGuid();

        await _consumer.Consume(Delivered.Of(Event(customerId, Guid.NewGuid())));

        var file = (await AllFiles()).Should().ContainSingle().Subject;
        file.CustomerId.Should().Be(customerId);
        file.TenantId.Should().Be(_tenantId);
        file.Channel.Should().Be(KycChannel.LeadConversion);
        file.Status.Should().Be(KycFileStatus.Collecting);
    }

    [Fact]
    public async Task The_agent_who_converted_the_lead_is_the_one_held_accountable_for_the_file()
    {
        var converter = Guid.NewGuid();

        await _consumer.Consume(Delivered.Of(Event(Guid.NewGuid(), converter)));

        (await AllFiles()).Single().CreatedBy.Should().Be(converter);
    }

    [Fact]
    public async Task A_redelivery_of_the_same_event_does_not_open_a_second_file()
    {
        var evt = Event(Guid.NewGuid(), Guid.NewGuid());

        await _consumer.Consume(Delivered.Of(evt));
        await _consumer.Consume(Delivered.Of(evt));

        (await AllFiles()).Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_triggers_for_one_converted_lead_leave_exactly_one_file(bool clientFirst)
    {
        // The redundancy is deliberate — each trigger must be able to open the file on its own —
        // so what has to hold is that the pair never produces two. Both orders, because neither
        // consumer is guaranteed to win: the outbox and the broker decide.
        var customerId = Guid.NewGuid();
        var leadId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var clientCreated = new ClientCreatedEvent(
            TenantId: _tenantId,
            ClientId: customerId,
            ClientNumber: "CLI-000042",
            ClientType: "Individual",
            AgencyId: Guid.NewGuid(),
            AdvisorUserId: null,
            SourceLeadId: leadId,
            CreatedBy: agentId);

        var clientConsumer = new ClientCreatedKycConsumer(
            new FixedScopeFactory(_sp), NullLogger<ClientCreatedKycConsumer>.Instance);

        if (clientFirst)
        {
            await clientConsumer.Consume(Delivered.Of(clientCreated));
            await _consumer.Consume(Delivered.Of(Event(customerId, agentId)));
        }
        else
        {
            await _consumer.Consume(Delivered.Of(Event(customerId, agentId)));
            await clientConsumer.Consume(Delivered.Of(clientCreated));
        }

        var file = (await AllFiles()).Should().ContainSingle().Subject;
        // Whichever ran first, the channel is the same — ClientCreatedKycConsumer derives
        // LeadConversion from SourceLeadId, so the two triggers cannot disagree about provenance.
        file.Channel.Should().Be(KycChannel.LeadConversion);
        file.CreatedBy.Should().Be(agentId);
    }

    [Fact]
    public async Task The_SYSTEM_placeholder_tenant_opens_nothing()
    {
        await _consumer.Consume(
            Delivered.Of(Event(Guid.NewGuid(), Guid.NewGuid(), tenantId: Guid.Empty)));

        (await AllFiles()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_customer_of_another_tenant_gets_its_own_file_and_leaves_the_first_alone()
    {
        var customerId = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();

        await _consumer.Consume(Delivered.Of(Event(customerId, Guid.NewGuid())));
        var firstFileId = (await AllFiles()).Single().Id;

        await _consumer.Consume(
            Delivered.Of(Event(customerId, Guid.NewGuid(), tenantId: otherTenant)));

        var files = await AllFiles();
        files.Should().HaveCount(2);
        files.Select(f => f.TenantId).Should().BeEquivalentTo(new[] { _tenantId, otherTenant });
        files.Should().Contain(f => f.Id == firstFileId, "the first tenant's file is untouched");
    }

    [Fact]
    public async Task A_failing_command_is_swallowed_so_the_broker_does_not_loop()
    {
        var act = async () => await _consumer.Consume(
            Delivered.Of(Event(Guid.Empty, Guid.NewGuid())));

        await act.Should().NotThrowAsync();
        (await AllFiles()).Should().BeEmpty();
    }
}
