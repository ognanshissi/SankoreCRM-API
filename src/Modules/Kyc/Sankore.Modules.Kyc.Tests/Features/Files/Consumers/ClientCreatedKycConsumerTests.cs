namespace Sankore.Modules.Kyc.Tests.Features.Files.Consumers;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files.Consumers;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// Drives the consumer exactly as MassTransit would — a <see cref="ConsumeContext{T}"/> carrying
/// the event — over the REAL <c>CreateKycFileHandler</c> reached through a real MediatR container.
/// Only the outbox publisher and the bus are substituted; everything that decides whether a file
/// exists, and with which channel and actor, is production code.
/// </summary>
public sealed class ClientCreatedKycConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly ServiceProvider _sp;
    private readonly ClientCreatedKycConsumer _consumer;

    public ClientCreatedKycConsumerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
        _sp = ConsumerTestContainer.Build(_db);
        _consumer = new ClientCreatedKycConsumer(
            new FixedScopeFactory(_sp), NullLogger<ClientCreatedKycConsumer>.Instance);
    }

    public void Dispose()
    {
        _sp.Dispose();
        _db.Dispose();
        _factory.Dispose();
    }

    private ClientCreatedEvent Event(
        Guid clientId, Guid createdBy, Guid? sourceLeadId = null, Guid? tenantId = null)
        => new(
            TenantId: tenantId ?? _tenantId,
            ClientId: clientId,
            ClientNumber: "CLI-000001",
            ClientType: "Individual",
            AgencyId: _agencyId,
            AdvisorUserId: null,
            SourceLeadId: sourceLeadId,
            CreatedBy: createdBy);

    private Task<List<KycFile>> AllFiles()
        => _db.KycFiles.IgnoreQueryFilters().ToListAsync();

    [Fact]
    public async Task A_client_created_without_a_lead_gets_a_file_on_the_Agency_channel()
    {
        var clientId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await _consumer.Consume(Delivered.Of(Event(clientId, agentId)));

        var file = (await AllFiles()).Should().ContainSingle().Subject;
        file.CustomerId.Should().Be(clientId);
        file.TenantId.Should().Be(_tenantId);
        file.Channel.Should().Be(KycChannel.Agency);
        file.Status.Should().Be(KycFileStatus.Collecting);
    }

    [Fact]
    public async Task The_file_is_filed_under_the_agency_M01_holds_the_customer_in()
    {
        var clientId = Guid.NewGuid();

        await _consumer.Consume(Delivered.Of(Event(clientId, Guid.NewGuid())));

        var file = (await AllFiles()).Should().ContainSingle().Subject;

        // From M01's summary, NOT from the event's AgencyId and not from the creating agent: a
        // head-office officer opening a branch customer's file must not move that file to head
        // office. The event carries a different agency on purpose here, so a handler reading the
        // wrong source fails this test.
        file.AgencyId.Should().Be(ConsumerTestContainer.DefaultAgencyId);
        file.AgencyId.Should().NotBe(_agencyId);
    }

    [Fact]
    public async Task A_customer_M01_cannot_resolve_still_gets_a_file_with_no_agency()
    {
        using var sp = ConsumerTestContainer.Build(_db, clientKnown: false);
        var consumer = new ClientCreatedKycConsumer(
            new FixedScopeFactory(sp), NullLogger<ClientCreatedKycConsumer>.Instance);

        await consumer.Consume(Delivered.Of(Event(Guid.NewGuid(), Guid.NewGuid())));

        var file = (await AllFiles()).Should().ContainSingle(
            "a hiccup in another module must not deny a customer the KYC file they are entitled to").Subject;

        // Null, not Guid.Empty: the perimeter treats null as "unrestricted callers only", which
        // fails closed. Guid.Empty would be a perimeter nobody belongs to that looks like a value.
        file.AgencyId.Should().BeNull();
    }

    [Fact]
    public async Task A_client_created_from_a_lead_gets_a_file_on_the_LeadConversion_channel()
    {
        // SourceLeadId is the only provenance the event carries, and the channel is evidence an
        // auditor reads — a client born of a conversion must not be recorded as a counter walk-in.
        var clientId = Guid.NewGuid();

        await _consumer.Consume(
            Delivered.Of(Event(clientId, Guid.NewGuid(), sourceLeadId: Guid.NewGuid())));

        var file = (await AllFiles()).Should().ContainSingle().Subject;
        file.Channel.Should().Be(KycChannel.LeadConversion);
    }

    [Fact]
    public async Task The_actor_of_the_event_is_the_one_held_accountable_for_the_file()
    {
        // Not a SYSTEM placeholder: a file nobody is accountable for is a file nobody completes.
        var clientId = Guid.NewGuid();
        var creator = Guid.NewGuid();

        await _consumer.Consume(Delivered.Of(Event(clientId, creator)));

        (await AllFiles()).Single().CreatedBy.Should().Be(creator);
    }

    [Fact]
    public async Task A_redelivery_of_the_same_event_does_not_open_a_second_file()
    {
        // At-least-once delivery is the contract: the broker WILL replay. Idempotency lives in the
        // command, so the consumer only has to not fight it.
        var evt = Event(Guid.NewGuid(), Guid.NewGuid());

        await _consumer.Consume(Delivered.Of(evt));
        await _consumer.Consume(Delivered.Of(evt));

        (await AllFiles()).Should().ContainSingle();
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
        // Same customer id under two tenants: the dedupe read is tenant-scoped, so neither tenant
        // can see — or suppress — the other's file. A cross-tenant match here would be a leak.
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
        // An empty ClientId cannot produce a file (KycFile.Open refuses it). Rethrowing would send
        // the message back to the broker forever for an event that can never succeed.
        var act = async () => await _consumer.Consume(
            Delivered.Of(Event(Guid.Empty, Guid.NewGuid())));

        await act.Should().NotThrowAsync();
        (await AllFiles()).Should().BeEmpty();
    }
}

/// <summary>What MassTransit hands a consumer once the outbox published the event.</summary>
internal static class Delivered
{
    public static ConsumeContext<T> Of<T>(T evt) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(evt);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }
}

/// <summary>The consumer creates its own DI scope; here every scope is the same container.</summary>
internal sealed class FixedScopeFactory(IServiceProvider sp) : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new Scope(sp);

    private sealed class Scope(IServiceProvider sp) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = sp;
        public void Dispose() { }
    }
}

/// <summary>
/// The minimum the real <c>CreateKycFileHandler</c> needs: its DbContext, a clock, and the KEYED
/// outbox publisher it resolves with <c>[FromKeyedServices(nameof(KycDbContext))]</c> — an
/// unkeyed registration is invisible to that attribute and the handler would fail to construct.
/// </summary>
internal static class ConsumerTestContainer
{
    /// <summary>The agency every seeded client belongs to, unless a test says otherwise.</summary>
    public static readonly Guid DefaultAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000aaaa");

    /// <param name="agencyId">
    /// What M01 answers for the customer. <c>null</c> stands for a customer M01 cannot resolve at
    /// all — an archived client, or a race — which must open the file anyway, without an agency.
    /// </param>
    public static ServiceProvider Build(KycDbContext db, Guid? agencyId = null, bool clientKnown = true)
    {
        var services = new ServiceCollection();

        services.AddSingleton(db);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(CustomersModule(agencyId ?? DefaultAgencyId, clientKnown));
        services.AddKeyedSingleton(nameof(KycDbContext), Substitute.For<IEventPublisher>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        // Real handlers, no pipeline behaviors: validation, audit and transactions belong to the
        // bootstrapper and are exercised there.
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(KycModule).Assembly));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The agency a KYC file is filed under comes from M01, not from the agent who opened it. Only
    /// the summary matters here, so the rest of the contract is left unimplemented.
    /// </summary>
    private static ICustomersModule CustomersModule(Guid agencyId, bool clientKnown)
    {
        var customers = Substitute.For<ICustomersModule>();

        customers.GetClientSummaryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(clientKnown
                ? new ClientSummary(
                    Id: Guid.NewGuid(),
                    ClientNumber: "AG000001-2026-000001",
                    ClientType: "Individual",
                    DisplayName: "DEMO CLIENT",
                    Status: "Active",
                    AgencyId: agencyId,
                    AdvisorUserId: null,
                    KycStatus: "Pending",
                    RiskLevel: "Standard",
                    MergedIntoId: null)
                : null);

        return customers;
    }
}
