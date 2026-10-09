namespace Sankore.Modules.Integration.Tests.Features.Balance;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Balance;
using Sankore.Modules.Integration.Features.References.GetReference;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Commands;

/// <summary>
/// The collaborators INT-15's suites share: an account port that RECORDS what it was asked (the
/// absence of a call is the property most of these tests assert), a distributed cache that can be
/// absent, present or broken, and a facade wired over the two.
///
/// <para>
/// The resolver and the clock come from <see cref="CommandsTestHarness"/> rather than being
/// rebuilt here: a second definition of "an active Fake connection" would let the two suites drift
/// on what an active connection is.
/// </para>
/// </summary>
internal static class BalanceTestHarness
{
    internal static readonly Guid TenantId = new("aaaaaaaa-0000-0000-0000-00000000000f");

    internal static readonly Guid CrmCustomerId = new("bbbbbbbb-0000-0000-0000-00000000000f");

    /// <summary>The account reference every test asks about, as the CBS spells it.</summary>
    internal const string AccountId = "CBS-ACC-0001";

    /// <summary>The human-readable number of the same account, as a snapshot carries it.</summary>
    internal const string AccountNumber = "CI001 0100 0001";

    /// <summary>
    /// How a snapshot's <c>accounts</c> column is written. The same options the facade reads it
    /// with — a test that serialised PascalCase would prove the reader works on data nothing
    /// produces.
    /// </summary>
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// An ACTIVE core-banking connection whose breaker thresholds are explicit, so a test can open
    /// the circuit with a known number of failures.
    /// </summary>
    internal static IntegrationConnection Connection(
        Guid? tenantId = null,
        int failureThreshold = 2,
        int breakSeconds = 30,
        Guid? id = null)
    {
        var clock = new CommandsTestHarness.FixedClock(CommandsTestHarness.Now);

        var connection = IntegrationConnection.Create(
            tenantId: tenantId ?? TenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "Fake CBS",
            settings: new FakeSettings
            {
                Family = IntegrationFamily.CoreBanking,
                CircuitBreakerFailureThreshold = failureThreshold,
                CircuitBreakerBreakSeconds = breakSeconds,
                TimeoutSeconds = 30,
            },
            createdBy: CommandsTestHarness.Actor,
            clock: clock,
            id: id ?? Guid.NewGuid());

        connection.RecordHealth(
            IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(40), CommandsTestHarness.Now), clock);

        var activated = connection.Activate(CommandsTestHarness.Actor, clock);
        if (activated.IsFailure)
            throw new InvalidOperationException($"Test fixture could not activate: {activated.Error}");

        return connection;
    }

    /// <summary>
    /// The snapshot INT-21 would have written: one account, with a balance deliberately different
    /// from the live one so a test can tell which of the two it is looking at.
    /// </summary>
    internal static CbsSnapshot Snapshot(
        Guid connectionId,
        Guid? tenantId = null,
        Guid? crmCustomerId = null,
        decimal balance = 75_000m,
        string accountId = AccountId,
        string accountNumber = AccountNumber,
        DateTimeOffset? snapshotAt = null)
    {
        var clock = new CommandsTestHarness.FixedClock(snapshotAt ?? CommandsTestHarness.Now.AddHours(-9));

        var snapshot = CbsSnapshot.Create(
            tenantId ?? TenantId, crmCustomerId ?? CrmCustomerId, connectionId, clock);

        var accounts = new List<CbsAccount>
        {
            new(
                AccountId: new ExternalId(accountId),
                AccountNumber: accountNumber,
                ProductCode: "EPARGNE",
                ProductLabel: "Compte épargne",
                Currency: "XOF",
                Balance: balance,
                AvailableBalance: balance,
                Status: "Active",
                OpenedOn: new DateOnly(2024, 1, 15)),
        };

        snapshot.Update(
            accountsJson: JsonSerializer.Serialize(accounts, SnapshotJson),
            loansJson: "[]",
            totalBalance: balance,
            monthlyFlow: 0m,
            kycLevelInCbs: KycLevel.Full,
            clock: clock);

        return snapshot;
    }

    /// <summary>The INT-07 row that makes an account ours without any snapshot at all.</summary>
    internal static IntegrationReference Reference(
        Guid connectionId,
        Guid? tenantId = null,
        Guid? crmCustomerId = null,
        string accountId = AccountId)
        => IntegrationReference.Create(
            tenantId: tenantId ?? TenantId,
            connectionId: connectionId,
            kind: IntegrationKind.Fake,
            entityType: IntegrationEntityTypes.Account,
            crmId: crmCustomerId ?? CrmCustomerId,
            externalId: accountId,
            clock: new CommandsTestHarness.FixedClock(CommandsTestHarness.Now));

    /// <summary>A live balance the recording port can answer with.</summary>
    internal static CbsBalance LiveBalance(
        decimal balance = 125_000m, string accountId = AccountId, DateTimeOffset? asOf = null)
        => new(
            AccountId: new ExternalId(accountId),
            Currency: "XOF",
            Balance: balance,
            AvailableBalance: balance - 5_000m,
            AsOf: asOf ?? CommandsTestHarness.Now,
            IsStale: false);

    internal static LiveBalanceGate Gate(
        IDistributedCache? cache = null, IntegrationResiliencePipelineProvider? pipelines = null)
        => new(
            cache,
            pipelines ?? new IntegrationResiliencePipelineProvider(),
            NullLogger<LiveBalanceGate>.Instance);

    /// <summary>
    /// The facade under test, with its real collaborators except the adapter and the gate.
    /// <paramref name="gate"/> left null is the "no cache, no breaker reading" container — which
    /// is a configuration that must still answer balances.
    /// </summary>
    internal static IntegrationModuleFacade Facade(
        IntegrationDbContext db,
        ICbsAdapter? adapter,
        LiveBalanceGate? gate = null,
        Guid? tenantId = null)
        => new(
            db,
            CommandsTestHarness.Resolver(db, adapter),
            new ReferenceLookup(db),
            CommandsTestHarness.Protector(),
            CommandsTestHarness.PayloadSource(tenantId ?? TenantId, CrmCustomerId),
            CommandsTestHarness.User(tenantId ?? TenantId),
            new CommandsTestHarness.FixedClock(CommandsTestHarness.Now),
            NullLogger<IntegrationModuleFacade>.Instance,
            gate);

    /// <summary>
    /// An adapter that answers balances and counts how often it was asked.
    ///
    /// <para>
    /// A recording double and not a substitute: "the second call does not reach the port" is an
    /// assertion about a call that did NOT happen, and a counter makes that readable where a
    /// <c>DidNotReceive</c> on a mock reads as the absence of a line.
    /// </para>
    /// </summary>
    internal sealed class RecordingAccountAdapter : ICbsAdapter, ICbsAccountPort
    {
        private readonly Func<ExternalId, IntegrationResult<CbsBalance>> _answer;

        public RecordingAccountAdapter(
            Func<ExternalId, IntegrationResult<CbsBalance>>? answer = null,
            CapabilityMode balanceMode = CapabilityMode.RealTime)
        {
            _answer = answer ?? (_ => IntegrationResult.Ok(LiveBalance()));

            Capabilities = new IntegrationCapabilities(
                new Dictionary<IntegrationCapability, CapabilityMode>
                {
                    [IntegrationCapability.ReadBalance] = balanceMode,
                });
        }

        public IntegrationKind Kind => IntegrationKind.Fake;

        public IntegrationCapabilities Capabilities { get; }

        /// <summary>Every account id the port was asked about, in order.</summary>
        public List<string> BalanceCalls { get; } = [];

        public Task<IntegrationHealth> CheckHealthAsync(
            IntegrationConnection connection, CancellationToken ct)
            => Task.FromResult(
                IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(10), CommandsTestHarness.Now));

        public Task<IntegrationResult<CbsBalance>> GetBalanceAsync(
            ExternalId accountId, CancellationToken ct)
        {
            BalanceCalls.Add(accountId.Value);
            return Task.FromResult(_answer(accountId));
        }

        public Task<IntegrationResult<ExternalId>> OpenAccountAsync(
            ExternalId customerId, string productCode, IdempotencyKey key, CancellationToken ct)
            => throw new NotSupportedException("INT-15 never opens an account.");

        public Task<IntegrationResult<IReadOnlyList<CbsAccount>>> GetAccountsAsync(
            ExternalId customerId, CancellationToken ct)
            => throw new NotSupportedException("INT-15 never lists accounts.");

        public Task<IntegrationResult<CbsDebitReceipt>> DebitAccountAsync(
            ExternalId accountId, decimal amount, string currency, string label,
            IdempotencyKey key, CancellationToken ct)
            => throw new NotSupportedException("INT-15 never moves money.");

        public Task<IntegrationResult<CbsDebitReceipt>> ReverseDebitAsync(
            ExternalId accountId, string originalReference, IdempotencyKey key, CancellationToken ct)
            => throw new NotSupportedException("INT-15 never moves money.");
    }

    /// <summary>
    /// An in-memory <see cref="IDistributedCache"/> that records every key it was given, and can
    /// be told to fail — which is how "Redis being down must not take the balance read down with
    /// it" is asserted rather than hoped for.
    /// </summary>
    internal sealed class RecordingCache(bool throwOnEveryCall = false) : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = [];

        public List<string> Reads { get; } = [];

        public List<string> Writes { get; } = [];

        /// <summary>The TTL the last write asked for — 60 seconds, absolute, is a criterion.</summary>
        public DistributedCacheEntryOptions? LastWriteOptions { get; private set; }

        public byte[]? Get(string key)
        {
            Reads.Add(key);

            if (throwOnEveryCall)
                throw new InvalidOperationException("Simulated Redis outage.");

            return _entries.TryGetValue(key, out var value) ? value : null;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Writes.Add(key);
            LastWriteOptions = options;

            if (throwOnEveryCall)
                throw new InvalidOperationException("Simulated Redis outage.");

            _entries[key] = value;
        }

        public Task SetAsync(
            string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _entries.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
