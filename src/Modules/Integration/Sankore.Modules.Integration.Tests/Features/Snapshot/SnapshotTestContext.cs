namespace Sankore.Modules.Integration.Tests.Features.Snapshot;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.Mappings;
using Sankore.Modules.Integration.Features.References.GetReference;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;

/// <summary>
/// Everything the INT-21 suite assembles, in one place.
///
/// <para>
/// The collaborators are REAL wherever the criterion depends on them: the genuine
/// <c>MappingResolver</c> over seeded rows (criterion 2 is about those rows), the genuine
/// <c>CallJournal</c> over a recording store (a call-log row is an assertion), the genuine
/// <c>OutboxEventPublisher</c> over the module's own context (criterion 4 is about an outbox row).
/// Only <see cref="IKycModule"/> is a substitute — it belongs to M02 and answers from
/// <c>kyc_settings</c>, which this module has no business building.
/// </para>
/// </summary>
internal sealed class SnapshotTestContext : IDisposable
{
    internal static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly Guid CrmCustomerId = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    /// <summary>The CRM product code the seeded mapping row folds the CBS savings code onto.</summary>
    internal const string CrmSavingsCode = "EPARGNE-CRM";

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    internal SnapshotTestContext()
    {
        Db = _factory.CreateContext();

        // An ACTIVE connection: the resolver only ever hands back active ones, so a test that
        // forgot this would be exercising the "no connection" branch without noticing.
        Db.Connections.Add(CommandsTestHarness.Connection(Tenant, id: ConnectionId));
        Db.SaveChanges();

        // The default M02 answer: a full-KYC customer over a 30-day window. Tests that care about
        // the tier or the window override it.
        WithLimits(nameof(KycLevel.Full), windowDays: 30);
    }

    internal IntegrationDbContext Db { get; }

    internal FakeAdapter Adapter { get; } = new();

    internal IKycModule Kyc { get; } = Substitute.For<IKycModule>();

    internal RecordingCallLogStore CallLog { get; } = new();

    internal TimeProvider Clock => CommandsTestHarness.Clock;

    internal DateTimeOffset Now => CommandsTestHarness.Now;

    /// <summary>What M02 answers for this customer. <c>null</c> means "no KYC file at all".</summary>
    internal void WithLimits(string? tier, int windowDays = 30)
    {
        Kyc.GetLimitsAsync(Tenant, CrmCustomerId, Arg.Any<CancellationToken>())
            .Returns(tier is null
                ? Task.FromResult<KycLimits?>(null)
                : Task.FromResult<KycLimits?>(new KycLimits(
                    Tier: tier,
                    IsCapped: !string.Equals(tier, nameof(KycLevel.Full), StringComparison.OrdinalIgnoreCase),
                    MaxBalance: 2_000_000m,
                    MaxFlow: 5_000_000m,
                    WindowDays: windowDays,
                    AlertPct: 80)));
    }

    /// <summary>
    /// The link INT-07 writes on a successful creation — and the thing whose absence IS
    /// "the customer does not exist in the CBS".
    /// </summary>
    /// <summary>
    /// Arranges the PORT path: the adapter itself reports the tier, which is what lets a
    /// divergence see a tier an officer changed inside the CBS.
    /// </summary>
    internal void SeedCbsReportedTier(KycLevel level, string externalId = FakeAdapter.SeededCustomerId)
        => Adapter.KycLevels[externalId] = level;

    /// <summary>
    /// Strips the optional read capability, leaving the adapter in the state a batch-file CBS is
    /// permanently in: it accepts the write and can be asked nothing.
    /// </summary>
    internal void WithoutKycLevelRead()
        => Adapter.Capabilities = new IntegrationCapabilities(
            Adapter.Capabilities.Modes
                .Where(e => e.Key != IntegrationCapability.ReadKycLevel)
                .ToDictionary(e => e.Key, e => e.Value));

    internal void SeedReference(string externalId = FakeAdapter.SeededCustomerId)
    {
        Db.References.Add(IntegrationReference.Create(
            tenantId: Tenant,
            connectionId: ConnectionId,
            kind: IntegrationKind.Fake,
            entityType: IntegrationEntityTypes.Customer,
            crmId: CrmCustomerId,
            externalId: externalId,
            clock: Clock));

        Db.SaveChanges();
    }

    internal void SeedProductMapping(string crmCode, string cbsCode)
    {
        Db.Mappings.Add(IntegrationMapping.Create(
            tenantId: Tenant,
            connectionId: ConnectionId,
            domain: MappingDomain.Product,
            crmCode: crmCode,
            externalCode: cbsCode,
            createdBy: CommandsTestHarness.Actor,
            clock: Clock));

        Db.SaveChanges();
    }

    /// <summary>
    /// A <c>SetKycLevel</c> command the CBS confirmed. This is the only statement of the tier the
    /// CBS holds that the fixed port contract makes available — see <c>CbsKycLevelReader</c>.
    /// </summary>
    /// <summary>
    /// Arranges the INFERENCE path: the tier is read from the last write the CBS acknowledged.
    ///
    /// <para>
    /// It also drops <see cref="IntegrationCapability.ReadKycLevel"/> from the adapter, and that
    /// is not incidental — the projector prefers the port whenever an adapter declares it, so a
    /// fixture that seeded an acknowledged command while leaving the capability on would be
    /// arranging one path and measuring the other. Seeding an acknowledged tier IS choosing the
    /// fallback, so the helper says so by construction. Use <see cref="SeedCbsReportedTier"/> for
    /// the port path.
    /// </para>
    /// </summary>
    internal void SeedAcknowledgedCbsTier(KycLevel level)
    {
        WithoutKycLevelRead();

        var payload = CommandsTestHarness.Protector().Protect(new SetKycLevelPayload(level));

        var command = IntegrationCommand.Create(
            tenantId: Tenant,
            connectionId: ConnectionId,
            commandType: CommandType.SetKycLevel,
            entityType: IntegrationEntityTypes.Customer,
            crmId: CrmCustomerId,
            idempotencyKey: new IdempotencyKey($"set-kyc-{level}"),
            createdBy: CommandsTestHarness.Actor,
            clock: Clock,
            payloadEncrypted: payload.Ciphertext,
            payloadFieldNames: payload.FieldNames);

        // Through the real transition table rather than by setting the column: a fixture that
        // reached Succeeded another way would not prove the reader filters on a status the
        // aggregate can actually be in.
        command.BeginSending(Clock);
        command.Succeed("CBS-ACK", Clock);

        Db.Commands.Add(command);
        Db.SaveChanges();
    }

    /// <summary>
    /// Makes only <c>GetMonthlyFlowAsync</c> fail, leaving accounts and loans answering —
    /// <see cref="FakeAdapter.ForcedReadOutcome"/> is all-or-nothing and cannot express it.
    /// </summary>
    internal void FailTheFlowRead() => _flowFails = true;

    private bool _flowFails;

    internal CbsSnapshot? Snapshot()
    {
        // A fresh context: asserting through the one the projector wrote with would read the
        // change tracker rather than the rows.
        using var read = _factory.CreateContext();

        return read.CbsSnapshots.FirstOrDefault(s => s.CrmCustomerId == CrmCustomerId);
    }

    internal int SnapshotCount()
    {
        using var read = _factory.CreateContext();
        return read.CbsSnapshots.Count();
    }

    internal List<Shared.Infrastructure.Outbox.OutboxMessage> OutboxMessages()
    {
        using var read = _factory.CreateContext();
        return [.. read.OutboxMessages];
    }

    internal ICbsSnapshotProjector Projector() => new CbsSnapshotProjector(
        db: Db,
        resolver: CommandsTestHarness.Resolver(
            Db, _flowFails ? new FlowFailingAdapter(Adapter) : Adapter),
        references: new ReferenceLookup(Db),
        translator: new SnapshotCodeTranslator(new MappingResolver(Db)),
        cbsKycLevels: new CbsKycLevelReader(Db, CommandsTestHarness.Protector()),
        journal: new CallJournal(CallLog, Clock, NullLogger<CallJournal>.Instance),
        kyc: Kyc,
        publisher: CommandsTestHarness.Publisher(Db),
        clock: Clock,
        logger: NullLogger<CbsSnapshotProjector>.Instance);

    internal Task ProjectAsync() => Projector().ProjectAsync(
        Tenant, ConnectionId, CrmCustomerId, CancellationToken.None);

    public void Dispose()
    {
        Db.Dispose();
        _factory.Dispose();
    }
}

/// <summary>
/// The <see cref="FakeAdapter"/> with ONE leg broken: the monthly flow fails, everything else
/// answers.
///
/// <para>
/// A wrapper and not a knob on the fake, because the fake's forced outcomes are deliberately
/// all-or-nothing (reads, writes, or everything) and the branch under test is specifically "some
/// legs answered and one did not". It delegates every member it does not break, so the test still
/// exercises the real resolution path — keyed DI, the capability matrix, the ports.
/// </para>
/// </summary>
internal sealed class FlowFailingAdapter(FakeAdapter inner)
    : ICbsAdapter, ICbsAccountPort, ICbsLoanPort, ICbsTransactionPort
{
    public IntegrationKind Kind => inner.Kind;

    public IntegrationCapabilities Capabilities => inner.Capabilities;

    /// <summary>
    /// Forwarded with its argument, not answered from a snapshot. A decorator that collapsed the
    /// connection would make the wrapped fake look like an adapter that cannot tell two
    /// connections apart — which is the defect <c>CapabilitiesFor</c> was introduced to end, and a
    /// test double is the last place to reintroduce it.
    /// </summary>
    public IntegrationCapabilities CapabilitiesFor(IntegrationConnection connection)
        => inner.CapabilitiesFor(connection);

    public Task<IntegrationHealth> CheckHealthAsync(IntegrationConnection connection, CancellationToken ct)
        => inner.CheckHealthAsync(connection, ct);

    public Task<IntegrationResult<CbsMonthlyFlow>> GetMonthlyFlowAsync(
        ExternalId customerId, YearMonth month, CancellationToken ct)
        => Task.FromResult(IntegrationResult.Transient<CbsMonthlyFlow>(IntegrationErrors.Timeout));

    public Task<IntegrationResult<IReadOnlyList<CbsAccount>>> GetAccountsAsync(
        ExternalId customerId, CancellationToken ct) => inner.GetAccountsAsync(customerId, ct);

    public Task<IntegrationResult<IReadOnlyList<CbsLoan>>> GetLoansAsync(
        ExternalId customerId, CancellationToken ct) => inner.GetLoansAsync(customerId, ct);

    public Task<IntegrationResult<CbsBalance>> GetBalanceAsync(ExternalId accountId, CancellationToken ct)
        => inner.GetBalanceAsync(accountId, ct);

    public Task<IntegrationResult<ExternalId>> OpenAccountAsync(
        ExternalId customerId, string productCode, IdempotencyKey key, CancellationToken ct)
        => inner.OpenAccountAsync(customerId, productCode, key, ct);

    public Task<IntegrationResult<CbsDebitReceipt>> DebitAccountAsync(
        ExternalId accountId, decimal amount, string currency, string label,
        IdempotencyKey key, CancellationToken ct)
        => inner.DebitAccountAsync(accountId, amount, currency, label, key, ct);

    public Task<IntegrationResult<CbsDebitReceipt>> ReverseDebitAsync(
        ExternalId accountId, string originalReference, IdempotencyKey key, CancellationToken ct)
        => inner.ReverseDebitAsync(accountId, originalReference, key, ct);

    public Task<IntegrationResult<CbsPage<CbsTransaction>>> GetTransactionsAsync(
        ExternalId accountId, DateOnly from, DateOnly to, string? cursor, CancellationToken ct)
        => inner.GetTransactionsAsync(accountId, from, to, cursor, ct);

    public Task<IntegrationResult<ExternalId>> SubmitLoanApplicationAsync(
        CbsLoanApplicationPayload payload, IdempotencyKey key, CancellationToken ct)
        => inner.SubmitLoanApplicationAsync(payload, key, ct);
}

/// <summary>
/// Collects the journal's rows instead of writing them, exactly as <c>CallJournalTests</c> does.
/// The real store opens a child DI scope, which a unit test has no container for — and what is
/// being asserted here is that the projector journalled at all, not how the row reaches Postgres.
/// </summary>
internal sealed class RecordingCallLogStore : ICallLogStore
{
    internal List<IntegrationCallLog> Rows { get; } = [];

    public Task AppendAsync(IntegrationCallLog row, CancellationToken ct)
    {
        Rows.Add(row);
        return Task.CompletedTask;
    }
}
