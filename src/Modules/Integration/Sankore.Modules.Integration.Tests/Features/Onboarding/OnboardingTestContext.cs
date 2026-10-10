namespace Sankore.Modules.Integration.Tests.Features.Onboarding;

using System.Text.Json;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.Features.Onboarding;
using Sankore.Modules.Integration.Features.References.GetReference;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Crypto;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Shared.Kernel;

/// <summary>
/// The whole INT-14 chain, wired the way the host wires it, over one InMemory database and one
/// <see cref="FakeAdapter"/>.
///
/// <para>
/// It is a harness and not a set of substitutes on purpose. The thing INT-14 asserts is that four
/// pieces agree — the consumer, the facade's enqueue, the dispatcher's execution and the outbox —
/// and every one of those is the real type here. The only doubles are the two cross-module
/// contracts (<see cref="ICustomersModule"/>, <see cref="IKycModule"/>) and the core banking
/// system itself, which is exactly the boundary this module owns.
/// </para>
///
/// <para>
/// <see cref="ITenantContext"/> and <see cref="ICurrentUser"/> are registered as
/// <c>Program.cs</c> registers them — reading the ambient background context, and THROWING when
/// there is none. That is deliberate: a consumer that created its DI scope before calling
/// <c>BackgroundJobContext.SetScope</c> fails here instead of silently onboarding into
/// <c>Guid.Empty</c>.
/// </para>
/// </summary>
internal sealed class OnboardingTestContext : IDisposable
{
    internal static readonly DateTimeOffset Now = CommandsTestHarness.Now;

    internal Guid TenantId { get; } = new("aaaaaaaa-0000-0000-0000-000000000001");

    internal Guid CrmCustomerId { get; } = new("bbbbbbbb-0000-0000-0000-000000000002");

    internal Guid ConnectionId { get; } = new("cccccccc-0000-0000-0000-000000000003");

    /// <summary>The stand-in core banking system. One instance for the whole chain, so the
    /// customer created at step one is still there when the account is opened at step three.</summary>
    internal FakeAdapter Adapter { get; }

    /// <summary>
    /// The inbox, with the real semantics and none of the storage.
    ///
    /// <para>
    /// A double rather than <c>IntegrationInboxGuard</c>, for the reason M01's suite documents on
    /// its own <c>FakeInboxGuard</c>: the guarantee is a PRIMARY KEY, and the EF InMemory provider
    /// enforces no key — its duplicate insert surfaces as a bare
    /// <see cref="ArgumentException"/> from the store rather than as the
    /// <c>DbUpdateException</c> the real guard catches. What INT-14 has to prove is that these
    /// consumers derive a per-consumer id and return when the claim is refused, and that is
    /// exactly what this records.
    /// </para>
    /// </summary>
    internal RecordingInboxGuard Inbox { get; } = new();

    internal ICustomersModule Customers { get; }

    internal IKycModule Kyc { get; }

    private readonly string _databaseName = $"onboarding-{Guid.NewGuid()}";
    private readonly ServiceProvider _provider;
    private readonly CommandsTestHarness.FixedClock _clock = new(Now);

    private OnboardingTestContext(FakeAdapter adapter, string? productCode, string? kycTier)
    {
        Adapter = adapter;
        Customers = Substitute.For<ICustomersModule>();
        Kyc = Substitute.For<IKycModule>();

        Customers.GetClientSummaryAsync(TenantId, CrmCustomerId, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(
                Id: CrmCustomerId,
                ClientNumber: "CRM-000123",
                ClientType: "Individual",
                DisplayName: "AWA OUATTARA",
                Status: "Active",
                AgencyId: new Guid("55555555-5555-5555-5555-555555555555"),
                AdvisorUserId: null,
                KycStatus: "Approved",
                RiskLevel: "Low",
                MergedIntoId: null));

        Customers.GetCustomerAsync(TenantId, CrmCustomerId, Arg.Any<CancellationToken>())
            .Returns(new CustomerSummary(
                CrmCustomerId, "AWA OUATTARA", "awa@example.ci", "+2250707070707", null));

        if (kycTier is not null)
            Kyc.GetLimitsAsync(TenantId, CrmCustomerId, Arg.Any<CancellationToken>())
                .Returns(new KycLimits(kycTier, kycTier != "Full", 2_000_000m, 500_000m, 30, 80));

        _provider = BuildProvider(productCode);
    }

    /// <summary>
    /// A context whose tenant has an ACTIVE core-banking connection — the ordinary installation.
    /// </summary>
    /// <param name="productCode">
    /// What <see cref="IOnboardingProductSelector"/> answers. <c>null</c> is the platform's real
    /// answer today (no contract exposes a chosen product), so it is the default.
    /// </param>
    internal static OnboardingTestContext WithActiveConnection(
        FakeAdapter? adapter = null,
        string? productCode = null,
        string? kycTier = "Full")
    {
        var context = new OnboardingTestContext(adapter ?? new FakeAdapter(), productCode, kycTier);
        context.SeedConnection();
        return context;
    }

    /// <summary>A tenant that has configured nothing — a fresh deployment.</summary>
    internal static OnboardingTestContext WithoutConnection()
        => new(new FakeAdapter(), productCode: null, kycTier: "Full");

    private void SeedConnection()
    {
        using var db = NewDb();

        db.Connections.Add(ConnectionFixture());
        db.SaveChanges();
    }

    private IntegrationConnection ConnectionFixture()
    {
        var connection = IntegrationConnection.Create(
            tenantId: TenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "CBS de test",
            settings: new FakeSettings { Family = IntegrationFamily.CoreBanking },
            createdBy: CommandsTestHarness.Actor,
            clock: _clock,
            id: ConnectionId);

        // Activation requires a passed health check — the aggregate's own rule, satisfied rather
        // than reached around.
        connection.RecordHealth(IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), Now), _clock);

        var activated = connection.Activate(CommandsTestHarness.Actor, _clock);
        if (activated.IsFailure)
            throw new InvalidOperationException($"Fixture could not activate: {activated.Error}");

        return connection;
    }

    /// <summary>A fresh context over the shared database, bound to the harness tenant.</summary>
    internal IntegrationDbContext NewDb() => NewDbFor(TenantId);

    internal IntegrationDbContext NewDbFor(Guid tenantId)
        => new(
            new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(_databaseName)
                .Options,
            new FixedTenantContext(tenantId));

    // ── Driving the chain ───────────────────────────────────────────────────

    /// <summary>Step one: M02 announces an approved file.</summary>
    internal Task ConsumeKycValidatedAsync(Guid messageId, Guid? tenantId = null, Guid? customerId = null)
        => new KycValidatedOnboardingConsumer(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<KycValidatedOnboardingConsumer>.Instance)
            .Consume(Context(
                new KycValidatedEvent(tenantId ?? TenantId, customerId ?? CrmCustomerId, Now),
                messageId));

    /// <summary>Step two: the chain reacts to the creation the dispatcher published.</summary>
    internal Task ConsumeCbsCustomerCreatedAsync(CbsCustomerCreatedEvent evt, Guid messageId)
        => new CbsCustomerCreatedOnboardingConsumer(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CbsCustomerCreatedOnboardingConsumer>.Instance)
            .Consume(Context(evt, messageId));

    /// <summary>
    /// What the dispatcher of INT-06 does to ONE queued command — the real
    /// <see cref="ExecuteIntegrationCommandHandler"/>, in its own fresh scope, as a job has.
    /// Returns the type it executed, or <c>null</c> when nothing was pending.
    /// </summary>
    /// <param name="only">
    /// Restricts the pick to one command type. The tests that force a failure at a precise step
    /// need it: this harness runs on a FROZEN clock, so two commands queued in the same save share
    /// a <c>CreatedAt</c> and the dispatcher's tie-break on the row id is a random Guid — which
    /// makes "execute the KYC level, then the account" undecidable without naming the type.
    /// </param>
    internal async Task<CommandType?> ExecuteNextAsync(CommandType? only = null)
    {
        Guid commandId;
        CommandType commandType;

        using (var read = NewDb())
        {
            var next = await read.Commands
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == TenantId && c.Status == CommandStatus.Pending)
                .Where(c => only == null || c.CommandType == only)
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Select(c => new { c.Id, c.CommandType })
                .FirstOrDefaultAsync();

            if (next is null) return null;

            commandId = next.Id;
            commandType = next.CommandType;
        }

        using var db = NewDb();

        var handler = CommandsTestHarness.ExecuteHandler(db, Adapter, TenantId, CrmCustomerId);

        var result = await handler.Handle(
            new ExecuteIntegrationCommandCommand(commandId, TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the handler records every outcome as a success of itself — see "
            + "ExecuteIntegrationCommandResult");

        return commandType;
    }

    /// <summary>Drains the queue, the way a dispatcher pass does.</summary>
    internal async Task<int> ExecutePendingCommandsAsync()
    {
        var executed = 0;

        while (await ExecuteNextAsync() is not null)
        {
            // A safety net rather than a rule of the chain: a bug that left a command Pending
            // would otherwise spin here instead of failing a test.
            if (++executed > 10)
                throw new InvalidOperationException(
                    "The chain queued more commands than INT-14 has steps.");
        }

        return executed;
    }

    // ── Reading what happened ───────────────────────────────────────────────

    internal async Task<List<IntegrationCommand>> CommandsAsync()
    {
        using var db = NewDb();

        return await db.Commands
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == TenantId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync();
    }

    /// <summary>
    /// The events the chain actually committed, read back out of the outbox the way the processor
    /// reads them — same type name, same serializer options. A test that asserted on a publisher
    /// substitute instead would pass even if the event never reached a row.
    /// </summary>
    internal async Task<List<TEvent>> PublishedAsync<TEvent>()
    {
        using var db = NewDb();

        var marker = typeof(TEvent).FullName!;

        var rows = await db.OutboxMessages
            .Where(m => m.EventType.StartsWith(marker))
            .OrderBy(m => m.OccurredAt)
            .Select(m => m.PayloadJson)
            .ToListAsync();

        return [.. rows.Select(json => JsonSerializer.Deserialize<TEvent>(json, OutboxJson.Options)!)];
    }

    public void Dispose() => _provider.Dispose();

    // ── Wiring ──────────────────────────────────────────────────────────────

    private ServiceProvider BuildProvider(string? productCode)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);

        // Exactly Program.cs's shape, including the absence of an HTTP fallback: these throw when
        // no ambient background scope was established, which is the whole reason a consumer must
        // call SetScope before CreateScope.
        services.AddScoped<ITenantContext>(_ =>
            BackgroundJobContext.CurrentTenant
            ?? throw new InvalidOperationException(
                "No ambient tenant: the consumer created its scope before establishing one."));

        services.AddScoped<ICurrentUser>(_ =>
            BackgroundJobContext.CurrentUser
            ?? throw new InvalidOperationException(
                "No ambient user: the consumer created its scope before establishing one."));

        services.AddDbContext<IntegrationDbContext>(
            opt => opt.UseInMemoryDatabase(_databaseName), ServiceLifetime.Scoped);

        // The module's own keyed encryptor, through its real registration: the payload of a
        // queued command is encrypted, and a pass-through double would hide a handler that forgot.
        services.AddIntegrationFieldProtection(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{IntegrationFieldProtection.SectionName}:FieldEncryptionKey"] = CommandsTestHarness.TestKey,
                [$"{IntegrationFieldProtection.SectionName}:BlindIndexKey"] = CommandsTestHarness.TestKey,
            })
            .Build());

        // Singleton: the chain spans several scopes and an inbox that forgot between them would
        // make every replay look like a first delivery.
        services.AddSingleton<IIntegrationInboxGuard>(Inbox);
        services.AddScoped<IntegrationAdapterResolver>();
        services.AddScoped<ReferenceLookup>();
        services.AddScoped<CommandPayloadProtector>();
        services.AddScoped<CbsCustomerPayloadSource>();
        services.AddSingleton(Customers);
        services.AddSingleton(Kyc);
        services.AddScoped<IIntegrationModule, IntegrationModuleFacade>();

        // Singleton and not scoped, unlike production: the chain spans several scopes and the
        // double has to remember the customer it created in the first one.
        services.AddKeyedSingleton<ICbsAdapter>(IntegrationKind.Fake.ToString(), Adapter);

        // BEFORE AddOnboardingServices so this wins its TryAdd — the same seam a module owning
        // the enrolment product choice would use.
        if (productCode is not null)
            services.AddScoped<IOnboardingProductSelector>(_ => new FixedProductSelector(productCode));

        services.AddOnboardingServices();

        return services.BuildServiceProvider();
    }

    private static ConsumeContext<TMessage> Context<TMessage>(TMessage message, Guid messageId)
        where TMessage : class
    {
        var context = Substitute.For<ConsumeContext<TMessage>>();
        context.Message.Returns(message);
        context.MessageId.Returns(messageId);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    /// <summary>
    /// One claim per (message, consumer), which is what
    /// <c>IntegrationInboxGuard.DeriveId(messageId, consumerKey)</c> produces against a real
    /// primary key.
    /// </summary>
    internal sealed class RecordingInboxGuard : IIntegrationInboxGuard
    {
        private readonly HashSet<(Guid MessageId, string ConsumerKey)> _claimed = [];

        internal int Claims { get; private set; }

        public Task<bool> TryBeginAsync(
            Guid messageId, Guid tenantId, string eventType, string consumerKey, CancellationToken ct)
        {
            Claims++;
            return Task.FromResult(_claimed.Add((messageId, consumerKey)));
        }

        /// <summary>Whether this (message, consumer) pair was ever granted.</summary>
        internal bool WasClaimedBy(Guid messageId, string consumerKey)
            => _claimed.Contains((messageId, consumerKey));
    }

    /// <summary>
    /// Stands in for the module that will one day own "which product did this customer subscribe
    /// to at enrolment". It exists so criterion 2's OpenAccount half is covered by a test instead
    /// of being unreachable code.
    /// </summary>
    private sealed class FixedProductSelector(string productCode) : IOnboardingProductSelector
    {
        public Task<string?> SelectAsync(Guid tenantId, Guid crmCustomerId, CancellationToken ct)
            => Task.FromResult<string?>(productCode);
    }
}
