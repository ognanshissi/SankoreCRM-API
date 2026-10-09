namespace Sankore.Modules.Integration.Tests.Features.Reconciliation;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Sankore.Modules.Integration.Tests.Features.Commands;

/// <summary>
/// The scaffolding the INT-34 suites share: contexts over a NAMED InMemory database (so several
/// runs of the job see the same rows, the way two nights do against PostgreSQL), a clock that can
/// be moved between runs, and doubles for the two modules whose answers the comparison reads.
///
/// <para>
/// The outbox publisher is the REAL one over the module's own context, never a recording double:
/// criterion 4 requires the summary to travel through the outbox in the run's own transaction,
/// and a double that recorded on the spot would report an event a failed commit never published.
/// </para>
/// </summary>
internal static class ReconciliationTestContext
{
    internal static readonly Guid Tenant = new("aaaaaaaa-0000-0000-0000-00000000000a");
    internal static readonly Guid OtherTenant = new("bbbbbbbb-0000-0000-0000-00000000000b");
    internal static readonly Guid ConnectionId = new("cccccccc-0000-0000-0000-00000000000c");

    /// <summary>Two successive nights, so a dedup test can tell the runs apart by their clock.</summary>
    internal static readonly DateTimeOffset NightOne = new(2026, 3, 11, 6, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset NightTwo = new(2026, 3, 12, 6, 0, 0, TimeSpan.Zero);

    internal static IntegrationDbContext NewDb(Guid tenantId, string databaseName)
        => new(
            new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options,
            new FixedTenantContext(tenantId));

    /// <summary>
    /// A service provider shaped like the API's, for the orchestrator that creates its own scope.
    /// <c>ITenantContext</c> is registered exactly as <c>Program.cs</c> registers it — reading the
    /// ambient background context — because that registration is the whole reason
    /// <c>BackgroundJobContext.SetScope</c> has to happen before <c>CreateScope</c>.
    /// </summary>
    internal static ServiceProvider NewProvider(string databaseName)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(NightOne));

        services.AddScoped<ITenantContext>(_ =>
            BackgroundJobContext.CurrentTenant
            ?? throw new InvalidOperationException(
                "No ambient tenant: the job created its scope before establishing one."));

        services.AddDbContext<IntegrationDbContext>(
            opt => opt.UseInMemoryDatabase(databaseName), ServiceLifetime.Scoped);

        return services.BuildServiceProvider();
    }

    /// <summary>An ACTIVE core-banking connection — the only kind the comparison reconciles.</summary>
    internal static IntegrationConnection Connection(Guid tenantId, Guid? id = null)
        => CommandsTestHarness.Connection(tenantId, id: id ?? ConnectionId);

    internal static IntegrationReference Reference(
        Guid tenantId, Guid crmCustomerId, string externalId, Guid? connectionId = null)
        => IntegrationReference.Create(
            tenantId: tenantId,
            connectionId: connectionId ?? ConnectionId,
            kind: IntegrationKind.Fake,
            entityType: IntegrationEntityTypes.Customer,
            crmId: crmCustomerId,
            externalId: externalId,
            clock: new FixedClock(NightOne));

    /// <summary>
    /// A snapshot carrying the one fact the comparison reads from it besides its own existence:
    /// the tier the CBS holds. The balances are left at zero on purpose — a reconciliation has no
    /// use for a customer's financial position, and a test that depended on one would be pinning
    /// the wrong thing.
    /// </summary>
    internal static CbsSnapshot Snapshot(
        Guid tenantId, Guid crmCustomerId, KycLevel? kycLevelInCbs, Guid? connectionId = null)
    {
        var clock = new FixedClock(NightOne);

        var snapshot = CbsSnapshot.Create(
            tenantId, crmCustomerId, connectionId ?? ConnectionId, clock);

        snapshot.Update("[]", "[]", 0m, 0m, kycLevelInCbs, clock);

        return snapshot;
    }

    /// <summary>M01's non-sensitive projection, with the only two fields INT-34 reads.</summary>
    internal static ClientSummary Client(
        Guid id, string status = "Active", Guid? mergedIntoId = null)
        => new(
            Id: id,
            ClientNumber: "CLI-0001",
            ClientType: "Individual",
            DisplayName: "Test client",
            Status: status,
            AgencyId: Guid.NewGuid(),
            AdvisorUserId: null,
            KycStatus: "Approved",
            RiskLevel: "Low",
            MergedIntoId: mergedIntoId);

    /// <summary>
    /// An M01 double that answers the batch read from a fixed table. Ids that are absent from
    /// the table are absent from the answer, which is exactly the contract's behaviour for an id
    /// that does not exist in the tenant.
    /// </summary>
    internal static ICustomersModule Customers(params ClientSummary[] clients)
    {
        var table = clients.ToDictionary(c => c.Id);
        var module = Substitute.For<ICustomersModule>();

        module.GetClientSummariesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var asked = (IReadOnlyCollection<Guid>)call[1];

                return Task.FromResult<IReadOnlyDictionary<Guid, ClientSummary>>(
                    asked.Where(table.ContainsKey).ToDictionary(id => id, id => table[id]));
            });

        return module;
    }

    /// <summary>
    /// An M02 double answering the same tier for every customer. <c>null</c> is the contract's
    /// "no KYC file at all", which <c>SnapshotKycLevels</c> folds to <see cref="KycLevel.None"/>.
    /// </summary>
    internal static IKycModule Kyc(string? tier)
    {
        var module = Substitute.For<IKycModule>();

        module.GetLimitsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<KycLimits?>(
                tier is null
                    ? null
                    : new KycLimits(
                        Tier: tier,
                        IsCapped: !string.Equals(tier, "Full", StringComparison.OrdinalIgnoreCase),
                        MaxBalance: 250_000m,
                        MaxFlow: 500_000m,
                        WindowDays: 30,
                        AlertPct: 80)));

        return module;
    }

    internal static OutboxEventPublisher<IntegrationDbContext> Publisher(IntegrationDbContext db)
        => new(db);

    /// <summary>Events of type <typeparamref name="T"/> that actually reached the outbox.</summary>
    internal static IReadOnlyList<T> Published<T>(IntegrationDbContext db)
        => [.. db.OutboxMessages
            .AsEnumerable()
            .Where(m => m.EventType.StartsWith(typeof(T).FullName!, StringComparison.Ordinal))
            .Select(m => JsonSerializer.Deserialize<T>(m.PayloadJson, OutboxJson.Options)!)];

    internal static TenantInfo TenantInfo(Guid id) => new(
        Id: id,
        Name: "Test tenant",
        Fqdn: $"{id:N}.sankore.test",
        IsActive: true,
        IsMaintenance: false,
        TrialExpiresAt: null,
        BlockedAt: null);

    internal static ICurrentUser User(Guid tenantId, Guid userId)
        => new TestUser(userId, tenantId);

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestUser(Guid id, Guid tenantId) : ICurrentUser
    {
        public Guid Id => id;

        public Guid TenantId => tenantId;

        public string DisplayName => "control officer";

        public bool IsAuthenticated => true;

        public IReadOnlyList<string> Roles => ["RegulationManager"];
    }

    /// <summary>Keeps every log entry, so a test can assert that something was reported.</summary>
    internal sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Add((logLevel, formatter(state, exception)));
    }
}
